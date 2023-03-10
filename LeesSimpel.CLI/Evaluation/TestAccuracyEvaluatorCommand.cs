using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NiceIO;
using Spectre.Console;
using Spectre.Console.Cli;

// ReSharper disable once ClassNeverInstantiated.Global
class TestAccuracyEvaluatorCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var testFiles = Directories.Backend.Combine("LeesSimpel.CLI/Evaluation/TestData").Files("*.json");

        var allTasks = new Dictionary<Task<TestResult>, (ProgressTask progressTask, NPath testFile)>();

        await AnsiConsole
            .Progress()
            .Columns(
                new SpinnerColumn(),
                new ElapsedTimeColumn(),
                new TaskDescriptionColumn()
            )
            .HideCompleted(false)
            .StartAsync(async ansiConsoleProgressContext =>
            {
                var activeJobs = new List<Task>();
                foreach (var testFile in testFiles)
                {
                    Task<TestResult> testTask = RunSingleTest(testFile);
                    var ansiConsoleTask = ansiConsoleProgressContext.AddTask(testFile.FileName);
                    ansiConsoleTask.IsIndeterminate = true;
                    ansiConsoleTask.MaxValue = 1;
                    allTasks[testTask] = (ansiConsoleTask,testFile);
                    activeJobs.Add(testTask);
                }

                while (activeJobs.Any())
                {
                    var completedTask = (Task<TestResult>)await Task.WhenAny(activeJobs);
                    activeJobs.Remove(completedTask);
                    var valueTuple = allTasks[completedTask];
                    var progressTask = valueTuple.progressTask;

                    var result = completedTask.Result;

                    var msg = result.Pass
                        ? $"[green] {valueTuple.testFile.FileName} [/] Correctly marked {result.PresentMessages.Count(m => m)} messages as present and {result.PresentMessages.Count(m => !m)} as missing"
                        : $"[red] {valueTuple.testFile.FileName} FAIL [/] Details below";

                    progressTask.Value = 1;
                    progressTask.Description = msg;
                    progressTask.StopTask();
                }
            });

        foreach (var kvp in allTasks)
        {
            if (kvp.Key.Result.Pass)
                continue;
            AnsiConsole.MarkupLine($"[red]FailureReason for [/]{kvp.Value.testFile.FileName}");
            foreach(var reason in kvp.Key.Result.FailureReasons)
                AnsiConsole.MarkupLine(reason);
            AnsiConsole.MarkupLine(kvp.Key.Result.DebugGtpOutput);
            AnsiConsole.WriteLine();
        }
        
        return 0;
    }

    class TestResult
    {
        public bool Pass;
        public string[] FailureReasons;
        public string DebugGtpOutput;
        public bool[] PresentMessages;
    }
    static async Task<TestResult> RunSingleTest(NPath testFile)
    {
        var stringReader = new StringReader(testFile.ReadAllText());
        var jsonReader = new JsonTextReader(stringReader);
        var testCaseObject = await JObject.LoadAsync(jsonReader);

        var summary = testCaseObject["summary_to_evaluate"].ToObject<Summary>();
        var propertyName = "evaluation_criteria";
        var evaluationCriteriaArray = testCaseObject[propertyName] as JArray ??
                                      throw new ArgumentException($"{propertyName} was not a JArray");
        var evaluationCriteria = AccuracyEvaluationCriteria.Parse(evaluationCriteriaArray);
        var evaluationResult = await AccuracyEvaluatorGTP3.Evaluate(summary, evaluationCriteria);
        bool[] expectedResult = testCaseObject["expected_results"].ToObject<bool[]>();

        var pass = Compare(evaluationResult.present_messges, expectedResult, evaluationCriteria, out var reasons);

        return new TestResult()
        {
            Pass = pass,
            DebugGtpOutput = evaluationResult.debug_gpt3_info, 
            FailureReasons = reasons,
            PresentMessages = evaluationResult.present_messges
        };
    }

    static bool Compare(bool[] actual, bool[] expected, AccuracyEvaluationCriteria criteria, out string[] reasons)
    {
        if (actual.Length != expected.Length)
        {
            reasons = new[] {$"Expected length of {expected.Length} but got {actual.Length}"};
            return false;
        }

        var reasonList = new List<string>();
        for (int i = 0; i != actual.Length; i++)
        {
            if (actual[i] == expected[i])
                continue;

            reasonList.Add($"Expected result is {expected[i]} for '{criteria.Things[i]}', but evaluator returned {actual[i]}");
        }

        reasons = reasonList.ToArray();
        return !reasons.Any();
    }
}