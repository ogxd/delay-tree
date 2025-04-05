using System.Collections.Concurrent;
using System.Diagnostics;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using RunMode = BenchmarkDotNet.Diagnosers.RunMode;

namespace Ogxd.DelayTree.Benchmarks;

public class CpuDiagnoserAttribute : Attribute, IConfigSource
{
    public IConfig Config { get; }

    public CpuDiagnoserAttribute()
    {
        var diagnoser = new CpuDiagnoser();
        Config = ManualConfig.CreateEmpty()
            .AddDiagnoser(diagnoser)
            // InProcess is required to get the CPU time via Process.GetCurrentProcess().TotalProcessorTime
            .AddJob(Job.InProcess);
    }
}

public class CpuDiagnoser : IDiagnoser
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _userStart;
    private long _timeStart;

    public TimeSpan TotalCpuTime { get; private set; }
    public TimeSpan TotalTime { get; private set; }

    public IEnumerable<string> Ids => new[] { "CPU" };

    public IEnumerable<IExporter> Exporters => Array.Empty<IExporter>();

    public IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();

    public RunMode GetRunMode(BenchmarkCase benchmarkCase) => RunMode.NoOverhead;

    public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
    {
        if (signal == HostSignal.BeforeActualRun)
        {
            _userStart = _process.TotalProcessorTime;
            _timeStart = Stopwatch.GetTimestamp();
        }

        if (signal == HostSignal.AfterActualRun)
        {
            TotalCpuTime = _process.TotalProcessorTime - _userStart;
            TotalTime = Stopwatch.GetElapsedTime(_timeStart);
        }
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
    {
        // double cpuUsagePercent = 100 * TotalCpuTime.TotalMilliseconds / TotalTime.TotalMilliseconds;
        // yield return new Metric(CpuPercentDescriptor.Instance, cpuUsagePercent);
        yield return new Metric(CpuTimeDescriptor.Instance, TotalCpuTime.TotalNanoseconds / results.TotalOperations);
    }

    public void DisplayResults(ILogger logger)
    {
    }

    public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
    {
        yield break;
    }

    private class CpuPercentDescriptor : IMetricDescriptor
    {
        internal static readonly IMetricDescriptor Instance = new CpuPercentDescriptor();

        public bool GetIsAvailable(Metric metric) => metric.Value > 0;

        public string Id => "CPU Usage (%)";

        public string DisplayName => Id;

        public string Legend => Id;

        public string NumberFormat => "#0.000";

        public UnitType UnitType => UnitType.Dimensionless;

        public string Unit => "%";

        public bool TheGreaterTheBetter => false;

        public int PriorityInCategory => 1;
    }

    private class CpuTimeDescriptor : IMetricDescriptor
    {
        internal static readonly IMetricDescriptor Instance = new CpuTimeDescriptor();

        public bool GetIsAvailable(Metric metric) => metric.Value > 0;

        public string Id => "CPU Time";

        public string DisplayName => Id;

        public string Legend => Id;

        public string NumberFormat => "#0.000";

        public UnitType UnitType => UnitType.Time;

        public string Unit => "ns";

        public bool TheGreaterTheBetter => false;

        public int PriorityInCategory => 1;
    }
}
