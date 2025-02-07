using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stryker.Core.CoverageAnalysis;
using Stryker.Core.Exceptions;
using Stryker.Core.Initialisation;
using Stryker.Core.Initialisation.Buildalyzer;
using Stryker.Core.Logging;
using Stryker.Core.Mutants;
using Stryker.Core.Options;
using Stryker.Core.ProjectComponents;
using Stryker.Core.Reporters;

namespace Stryker.Core.MutationTest
{
    public interface IMutationTestProcess
    {
        MutationTestInput Input { get; }
        void Mutate();
        StrykerRunResult Test(IEnumerable<Mutant> mutantsToTest);
        void Restore();
        void GetCoverage();
        void FilterMutants();
    }

    public class MutationTestProcess : IMutationTestProcess
    {
        private static readonly ILogger Logger = ApplicationLogging.LoggerFactory.CreateLogger<MutationTestProcess>();
        private readonly IProjectComponent _projectContents;
        private readonly IMutationTestExecutor _mutationTestExecutor;
        private readonly IReporter _reporter;
        private readonly ICoverageAnalyser _coverageAnalyser;
        private readonly StrykerOptions _options;
        private readonly IMutationProcess _mutationProcess;
        private static readonly Dictionary<Language, Func<MutationTestInput, StrykerOptions, IMutationProcess>> LanguageMap = new();

        static MutationTestProcess()
        {
            DeclareMutationProcessForLanguage<CsharpMutationProcess>(Language.Csharp);
            DeclareMutationProcessForLanguage<FsharpMutationProcess>(Language.Fsharp);
        }

        public static void DeclareMutationProcessForLanguage<T>(Language language) where T:IMutationProcess
        {
            var constructor = typeof(T).GetConstructor(new[]
                { typeof(MutationTestInput), typeof(StrykerOptions) });
            if (constructor == null)
            {
                throw new NotSupportedException(
                    $"Failed to find a constructor with the appropriate signature for type {typeof(T)}");
            }

            LanguageMap[language] = (x, y) => (IMutationProcess)constructor.Invoke(new object[] { x, y });
        }

        public MutationTestProcess(MutationTestInput input,
            StrykerOptions options,
            IReporter reporter,
            IMutationTestExecutor executor,
            IMutationProcess mutationProcess = null,
            ICoverageAnalyser coverageAnalyzer = null)
        {
            Input = input;
            _reporter = reporter;
            _options = options;
            _mutationTestExecutor = executor;
            _mutationProcess = mutationProcess ?? BuildMutationProcess();
            _coverageAnalyser = coverageAnalyzer ?? new CoverageAnalyser(_options);
            _projectContents = input.ProjectInfo.ProjectContents;
        }

        public MutationTestInput Input { get; }

        private IMutationProcess BuildMutationProcess()
        {
            if (!LanguageMap.ContainsKey(Input.ProjectInfo.ProjectUnderTestAnalyzerResult.GetLanguage()))
            {
                throw new GeneralStrykerException(
                    "no valid language detected || no valid csproj or fsproj was given.");
            }
            return LanguageMap[Input.ProjectInfo.ProjectUnderTestAnalyzerResult.GetLanguage()](Input, _options);
        }

        public void Mutate()
        {
            Input.ProjectInfo.BackupOriginalAssembly();
            _mutationProcess.Mutate();
        }

        public void FilterMutants() => _mutationProcess.FilterMutants();

        public StrykerRunResult Test(IEnumerable<Mutant> mutantsToTest)
        {
            if (!MutantsToTest(mutantsToTest))
            {
                return new StrykerRunResult(_options, double.NaN);
            }

            TestMutants(mutantsToTest);
            _mutationTestExecutor.TestRunner.Dispose();

            return new StrykerRunResult(_options, _projectContents.GetMutationScore());
        }

        public void Restore() => Input.ProjectInfo.RestoreOriginalAssembly();

        private void TestMutants(IEnumerable<Mutant> mutantsToTest)
        {
            var mutantGroups = BuildMutantGroupsForTest(mutantsToTest.ToList());

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _options.Concurrency };

            Parallel.ForEach(mutantGroups, parallelOptions, mutants =>
            {
                var reportedMutants = new HashSet<Mutant>();
                
                _mutationTestExecutor.Test(mutants,
                    Input.InitialTestRun.TimeoutValueCalculator,
                    (testedMutants, tests, ranTests, outTests) => TestUpdateHandler(testedMutants, tests, ranTests, outTests, reportedMutants));

                OnMutantsTested(mutants, reportedMutants);
            });
        }

        private bool TestUpdateHandler(IEnumerable<Mutant> testedMutants, ITestGuids failedTests, ITestGuids ranTests,
            ITestGuids timedOutTest, ISet<Mutant> reportedMutants)
        {
            var testsFailingInitially = Input.InitialTestRun.Result.FailingTests.GetGuids().ToHashSet();
            var continueTestRun = _options.OptimizationMode.HasFlag(OptimizationModes.DisableBail);
            if (testsFailingInitially.Count > 0 && failedTests.GetGuids().Any(id => testsFailingInitially.Contains(id)))
            {
                // some of the failing tests where failing without any mutation
                // we discard those tests
                failedTests = new TestGuidsList(
                    failedTests.GetGuids().Where(t => !testsFailingInitially.Contains(t)));
            }
            foreach (var mutant in testedMutants)
            {
                mutant.AnalyzeTestRun(failedTests, ranTests, timedOutTest, false);

                if (mutant.ResultStatus == MutantStatus.NotRun)
                {
                    continueTestRun = true; // Not all mutants in this group were tested so we continue
                }

                OnMutantTested(mutant, reportedMutants); // Report on mutant that has been tested
            }

            return continueTestRun;
        }

        private void OnMutantsTested(IEnumerable<Mutant> mutants, ISet<Mutant> reportedMutants)
        {
            foreach (var mutant in mutants)
            {
                if (mutant.ResultStatus == MutantStatus.NotRun)
                {
                    Logger.LogWarning($"Mutation {mutant.Id} was not fully tested.");
                }

                OnMutantTested(mutant, reportedMutants);
            }
        }

        private void OnMutantTested(Mutant mutant, ISet<Mutant> reportedMutants)
        {
            if (mutant.ResultStatus == MutantStatus.NotRun || reportedMutants.Contains(mutant))
            {
                // skip duplicates or useless notifications
                return;
            }
            _reporter?.OnMutantTested(mutant);
            reportedMutants.Add(mutant);
        }

        private static bool MutantsToTest(IEnumerable<Mutant> mutantsToTest)
        {
            if (!mutantsToTest.Any())
            {
                return false;
            }
            if (mutantsToTest.Any(x => x.ResultStatus != MutantStatus.NotRun))
            {
                throw new GeneralStrykerException("Only mutants to run should be passed to the mutation test process. If you see this message please report an issue.");
            }

            return true;
        }

        private IEnumerable<List<Mutant>> BuildMutantGroupsForTest(IReadOnlyCollection<Mutant> mutantsNotRun)
        {
            if (_options.OptimizationMode.HasFlag(OptimizationModes.DisableMixMutants) || !_options.OptimizationMode.HasFlag(OptimizationModes.CoverageBasedTest))
            {
                return mutantsNotRun.Select(x => new List<Mutant> { x });
            }

            var blocks = new List<List<Mutant>>(mutantsNotRun.Count);
            var mutantsToGroup = mutantsNotRun.ToList();
            // we deal with mutants needing full testing first
            blocks.AddRange(mutantsToGroup.Where(m => m.AssessingTests.IsEveryTest).Select(m => new List<Mutant> { m }));
            mutantsToGroup.RemoveAll(m => m.AssessingTests.IsEveryTest);

            mutantsToGroup = mutantsToGroup.Where(m => m.ResultStatus == MutantStatus.NotRun).ToList();

            var testsCount = Input.InitialTestRun.Result.RanTests.Count;
            mutantsToGroup = mutantsToGroup.OrderBy(m => m.AssessingTests.Count).ToList();
            while (mutantsToGroup.Count>0)
            {
                // we pick the first mutant
                var usedTests = mutantsToGroup[0].AssessingTests;
                var nextBlock = new List<Mutant> { mutantsToGroup[0] };
                mutantsToGroup.RemoveAt(0);
                for (var j = 0; j < mutantsToGroup.Count; j++)
                {
                    var currentMutant = mutantsToGroup[j];
                    var nextSet = currentMutant.AssessingTests;
                    if (nextSet.Count + usedTests.Count > testsCount)
                    {
                        break;
                    }

                    if (nextSet.ContainsAny(usedTests))
                    {
                        continue;
                    }

                    // add this mutant to the block
                    nextBlock.Add(currentMutant);
                    // remove the mutant from the list of mutants to group
                    mutantsToGroup.RemoveAt(j--);
                    // add this mutant's tests
                    usedTests = usedTests.Merge(nextSet);
                }

                blocks.Add(nextBlock);
            }
            
            Logger.LogDebug(
                $"Mutations will be tested in {blocks.Count} test runs" +
                (mutantsNotRun.Count > blocks.Count ? $", instead of {mutantsNotRun.Count}." : "."));

            return blocks;
        }

        public void GetCoverage() => _coverageAnalyser.DetermineTestCoverage(_mutationTestExecutor.TestRunner, _projectContents.Mutants, Input.InitialTestRun.Result.FailingTests);
    }
}
