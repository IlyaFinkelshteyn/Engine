﻿using R4nd0mApps.TddStud10.Common.Domain;
using R4nd0mApps.TddStud10.Engine.Core;
using R4nd0mApps.TddStud10.Logger;
using R4nd0mApps.TddStud10.TestRuntime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace R4nd0mApps.TddStud10.Engine
{
    public static class Engine
    {
        private static ILogger Logger = R4nd0mApps.TddStud10.Logger.LoggerFactory.logger;
        private static ITelemetryClient TelemetryClient = R4nd0mApps.TddStud10.Logger.TelemetryClientFactory.telemetryClient;

        public static RunStep[] CreateRunSteps(Func<DocumentLocation, IEnumerable<DTestCase>> findTest)
        {
            return new[]
            {
                TddStud10Runner.CreateRunStep(new RunStepInfo("Creating Solution Snapshot".ToRSN(), RunStepKind.Build, RunStepSubKind.CreateSnapshot), TakeSolutionSnapshot)
                , TddStud10Runner.CreateRunStep(new RunStepInfo("Deleting Build Output".ToRSN(), RunStepKind.Build, RunStepSubKind.DeleteBuildOutput), DeleteBuildOutput)
                , TddStud10Runner.CreateRunStep(new RunStepInfo("Building Solution Snapshot".ToRSN(), RunStepKind.Build, RunStepSubKind.BuildSnapshot), BuildSolutionSnapshot)
                , TddStud10Runner.CreateRunStep(new RunStepInfo("Refresh Test Runtime".ToRSN(), RunStepKind.Build, RunStepSubKind.RefreshTestRuntime), RefreshTestRuntime)
                , TddStud10Runner.CreateRunStep(new RunStepInfo("Discover Sequence Points".ToRSN(), RunStepKind.Build, RunStepSubKind.DiscoverSequencePoints), DiscoverSequencePoints)
                , TddStud10Runner.CreateRunStep(new RunStepInfo("Discover Unit Tests".ToRSN(), RunStepKind.Build, RunStepSubKind.DiscoverTests), DiscoverUnitTests)
                , TddStud10Runner.CreateRunStep(new RunStepInfo("Instrument Binaries".ToRSN(), RunStepKind.Build, RunStepSubKind.InstrumentBinaries), InstrumentBinaries(findTest))
                , TddStud10Runner.CreateRunStep(new RunStepInfo("Running Tests".ToRSN(), RunStepKind.Test, RunStepSubKind.RunTests), RunTests)
            };
        }

        public static void FindAndExecuteForEachAssembly(IRunExecutorHost host, string buildOutputRoot, DateTime timeFilter, Action<string> action, int? maxThreads = null)
        {
            int madDegreeOfParallelism = maxThreads.HasValue ? maxThreads.Value : Environment.ProcessorCount;
            Logger.LogInfo("FindAndExecuteForEachAssembly: Running with {0} threads.", madDegreeOfParallelism);
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dll", ".exe" };
            Parallel.ForEach(
                Directory.EnumerateFiles(buildOutputRoot, "*").Where(s => extensions.Contains(Path.GetExtension(s))),
                new ParallelOptions { MaxDegreeOfParallelism = madDegreeOfParallelism },
                assemblyPath =>
                {
                    if (!File.Exists(Path.ChangeExtension(assemblyPath, ".pdb")))
                    {
                        return;
                    }

                    var lastWriteTime = File.GetLastWriteTimeUtc(assemblyPath);
                    if (lastWriteTime < timeFilter)
                    {
                        return;
                    }

                    Logger.LogInfo("FindAndExecuteForEachAssembly: Running for assembly {0}. LastWriteTime: {1}.", assemblyPath, lastWriteTime.ToLocalTime());
                    action(assemblyPath);
                });
        }

        private static RunStepName ToRSN(this string name)
        {
            return RunStepName.NewRunStepName(name);
        }

        private static RunStepResult ToRSR(this RunStepStatus runStepStatus, RunData rd, string addendum)
        {
            return new RunStepResult(
                runStepStatus,
                rd,
                RunStepStatusAddendum.NewFreeFormatData(addendum));
        }

        private static RunStepResult RefreshTestRuntime(IRunExecutorHost host, RunStartParams rsp, RunStepInfo rsi)
        {
            var output = TestRunTimeInstaller.Install(rsp.Solution.BuildRoot.Item);

            return RunStepStatus.Succeeded.ToRSR(RunData.NoData, string.Format("Copied Test Runtime: {0}", output));
        }

        private static RunStepResult RunTests(IRunExecutorHost host, RunStartParams rsp, RunStepInfo rsi)
        {
            if (!host.CanContinue())
            {
                throw new OperationCanceledException();
            }

            var output = RunTestHost("execute", rsp);

            RunStepStatus rss = RunStepStatus.Succeeded;
            if (output.Item1 != 0)
            {
                rss = RunStepStatus.Failed;
            }

            var testResults = PerTestIdDResults.Deserialize(FilePath.NewFilePath(rsp.DataFiles.TestResultsStore.Item));
            var coverageSession = PerSequencePointIdTestRunId.Deserialize(FilePath.NewFilePath(rsp.DataFiles.CoverageSessionStore.Item));
            var testFailureInfo = PerDocumentLocationTestFailureInfo.Deserialize(FilePath.NewFilePath(rsp.DataFiles.TestFailureInfoStore.Item));

            return rss.ToRSR(RunData.NewTestRunOutput(testResults, testFailureInfo, coverageSession), output.Item2);
        }

        private static Tuple<int, string> RunTestHost(string command, RunStartParams rsp)
        {
            string testRunnerPath = rsp.TestHostPath.Item;
            var output = ExecuteProcess(
                testRunnerPath,
                Core.TestHost.buildCommandLine(command, rsp)
            );

            return output;
        }

        private static RunStepResult DiscoverSequencePoints(IRunExecutorHost host, RunStartParams rsp, RunStepInfo rsi)
        {
            var sequencePoint = Instrumentation.GenerateSequencePointInfo(host, rsp);
            if (sequencePoint != null)
            {
                sequencePoint.Serialize(rsp.DataFiles.SequencePointStore);
            }

            var totalSP = sequencePoint.Values.Aggregate(0, (acc, e) => acc + e.Count);
            TelemetryClient.TrackEvent(rsi.name.Item, new Dictionary<string, string>(), new Dictionary<string, double> { { "SequencePointCount", totalSP } });

            return RunStepStatus.Succeeded.ToRSR(RunData.NewSequencePoints(sequencePoint), "Binaries Instrumented - which ones - TBD");
        }

        private static RunStepResult DiscoverUnitTests(IRunExecutorHost host, RunStartParams rsp, RunStepInfo rsi)
        {
            if (!host.CanContinue())
            {
                throw new OperationCanceledException();
            }

            var output = RunTestHost("discover", rsp);

            RunStepStatus rss = RunStepStatus.Succeeded;
            if (output.Item1 != 0)
            {
                rss = RunStepStatus.Failed;
            }

            var testsPerAssembly = PerDocumentLocationDTestCases.Deserialize(FilePath.NewFilePath(rsp.DataFiles.DiscoveredUnitDTestsStore.Item));

            var totalTests = testsPerAssembly.Values.Aggregate(0, (acc, e) => acc + e.Count);
            TelemetryClient.TrackEvent(rsi.name.Item, new Dictionary<string, string>(), new Dictionary<string, double> { { "TestCount", totalTests } });

            return rss.ToRSR(RunData.NewTestCases(testsPerAssembly), "Unit Tests Discovered - which ones - TBD");
        }

        private static Func<IRunExecutorHost, RunStartParams, RunStepInfo, RunStepResult> InstrumentBinaries(Func<DocumentLocation, IEnumerable<DTestCase>> findTest)
        {
            return (host, rsp, rsi) =>
            {
                Instrumentation.Instrument(host, rsp, findTest);

                return RunStepStatus.Succeeded.ToRSR(RunData.NoData, "Binaries Instrumented - which ones - TBD");
            };
        }

        private static RunStepResult BuildSolutionSnapshot(IRunExecutorHost host, RunStartParams rsp, RunStepInfo rsi)
        {
            var output = ExecuteProcess(
                Path.Combine(
                    Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                    string.Format(@"MSBuild\{0}\Bin\msbuild.exe", host.HostVersion)),
                    string.Format(
                        @"/m /v:minimal /p:Configuration=Debug /p:CreateVsixContainer=false /p:DeployExtension=false /p:CopyVsixExtensionFiles=false /p:RunCodeAnalysis=false {0} /p:OutDir=""{1}\\"" ""{2}""",
                        string.Join(" ", (rsp.Config.AdditionalMSBuildProperties ?? new string[0]).Select(it => string.Format("/p:{0}", it))),
                        rsp.Solution.BuildRoot.Item,
                        rsp.Solution.SnapshotPath.Item)
            );

            RunStepStatus rss = RunStepStatus.Succeeded;
            if (output.Item1 != 0)
            {
                rss = RunStepStatus.Failed;
            }

            return rss.ToRSR(RunData.NoData, output.Item2);
        }

        private static RunStepResult TakeSolutionSnapshot(IRunExecutorHost host, RunStartParams rsp, RunStepInfo rsi)
        {
            var solutionGrandParentPath = Path.GetDirectoryName(Path.GetDirectoryName(rsp.Solution.Path.Item));
            var projects = VsSolution.GetProjects(host.HostVersion, rsp.Solution.Path.Item).ToList();

            var toCopy = new List<Tuple<string, SearchOption>>
            {
                new Tuple<string, SearchOption>(Path.GetDirectoryName(rsp.Solution.Path.Item), SearchOption.TopDirectoryOnly),
            };

            Array.ForEach(rsp.Config.SnapshotIncludeFolders,
                item =>
                {
                    toCopy.Add(new Tuple<string, SearchOption>(Path.Combine(Path.GetDirectoryName(rsp.Solution.Path.Item), item), SearchOption.AllDirectories));
                });

            projects.ForEach(p =>
            {
                var projectFile = Path.Combine(Path.GetDirectoryName(rsp.Solution.Path.Item), p.RelativePath);
                var folder = Path.GetDirectoryName(projectFile);
                toCopy.Add(new Tuple<string, SearchOption>(folder, SearchOption.AllDirectories));
            });

            toCopy.ForEach(item =>
            {
                if (!host.CanContinue())
                {
                    throw new OperationCanceledException();
                }

                CopyFiles(rsp, solutionGrandParentPath, item.Item1, item.Item2);
            });

            SnapshotGC.mark(FilePath.NewFilePath(Path.GetDirectoryName(rsp.Solution.SnapshotPath.Item)));

            TelemetryClient.TrackEvent(rsi.name.Item, new Dictionary<string, string>(), new Dictionary<string, double> { { "ProjectCount", projects.Count } });

            return RunStepStatus.Succeeded.ToRSR(RunData.NoData, "What was done - TBD");
        }

        private static void CopyFiles(RunStartParams rsp, string solutionGrandParentPath, string folder, SearchOption searchOpt)
        {
            if (!new DirectoryInfo(folder).Exists)
            {
                return;
            }

            foreach (var src in Directory.EnumerateFiles(folder, "*", searchOpt))
            {
                var shouldExlclude = rsp.Config.SnapshotExcludePatterns.Any(item => src.IndexOf(item, 0, StringComparison.OrdinalIgnoreCase) >= 0);
                if (shouldExlclude)
                {
                    continue;
                }

                var dst = src.ToUpperInvariant().Replace(solutionGrandParentPath.ToUpperInvariant(), rsp.Config.SnapShotRoot);
                var srcInfo = new FileInfo(src);
                var dstInfo = new FileInfo(dst);

                if (srcInfo.LastWriteTimeUtc > dstInfo.LastWriteTimeUtc)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    Logger.LogInfo("Copying: {0} - {1}.", src, dst);
                    File.Copy(src, dst, true);
                }
            }
        }

        private static RunStepResult DeleteBuildOutput(IRunExecutorHost host, RunStartParams rsp, RunStepInfo rsi)
        {
            if (Directory.Exists(rsp.Solution.BuildRoot.Item))
            {
                foreach (var file in Directory.EnumerateFiles(rsp.Solution.BuildRoot.Item, "*.pdb"))
                {
                    File.Delete(file);

                    var dll = Path.ChangeExtension(file, "dll");
                    if (File.Exists(dll))
                    {
                        File.Delete(dll);
                    }
                }
            }

            return RunStepStatus.Succeeded.ToRSR(RunData.NoData, "What was done - TBD");
        }

        private static Tuple<int, string> ExecuteProcess(string fileName, string arguments)
        {
            ConcurrentQueue<string> consoleOutput = new ConcurrentQueue<string>();
            var commandLine = string.Format("Executing: '{0}' '{1}'", fileName, arguments);
            consoleOutput.Enqueue(commandLine);
            Logger.LogInfo(commandLine);

            ProcessStartInfo processStartInfo;
            Process process;

            processStartInfo = new ProcessStartInfo();
            processStartInfo.CreateNoWindow = true;
            processStartInfo.RedirectStandardOutput = true;
            processStartInfo.RedirectStandardInput = true;
            processStartInfo.UseShellExecute = false;
            processStartInfo.Arguments = arguments;
            processStartInfo.FileName = fileName;

            process = new Process();
            process.StartInfo = processStartInfo;
            // enable raising events because Process does not raise events by default
            process.EnableRaisingEvents = true;
            // attach the event handler for OutputDataReceived before runStarting the process
            process.OutputDataReceived += new DataReceivedEventHandler
            (
                delegate (object sender, DataReceivedEventArgs e)
                {
                    var data = e.Data ?? "";
                    // append the new data to the data already read-in
                    consoleOutput.Enqueue(data);
                    Logger.LogInfo(data);
                }
            );
            process.ErrorDataReceived += new DataReceivedEventHandler
            (
                delegate (object sender, DataReceivedEventArgs e)
                {
                    var data = e.Data ?? "";
                    // append the new data to the data already read-in
                    consoleOutput.Enqueue(data);
                    Logger.LogError(data);
                }
            );
            // start the process
            // then begin asynchronously reading the output
            // then wait for the process to exit
            // then cancel asynchronously reading the output
            process.Start();
            process.BeginOutputReadLine();
            process.WaitForExit();
            process.CancelOutputRead();

            var sb = new StringBuilder();
            Array.ForEach(consoleOutput.ToArray(), s => sb.AppendLine(s));

            return new Tuple<int, string>(process.ExitCode, sb.ToString());
        }
    }
}
