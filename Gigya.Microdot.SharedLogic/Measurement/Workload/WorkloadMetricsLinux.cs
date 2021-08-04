﻿using System;
using System.Diagnostics;
using System.Linq;
using Gigya.Microdot.Interfaces.Logging;
using Gigya.Microdot.Interfaces.SystemWrappers;
using Gigya.Microdot.SharedLogic.Monitor;
using Metrics;
using Timer = System.Threading.Timer;

namespace Gigya.Microdot.SharedLogic.Measurement.Workload
{
    public sealed class WorkloadMetricsLinux : IWorkloadMetrics
    {
        private readonly AggregatingHealthStatus _healthStatus;
        private readonly Func<WorkloadMetricsConfig> _getConfig;
        private readonly IDateTime _dateTime;
        private readonly PerformanceEventListener _eventListener;

        private LowSensitivityHealthCheck _cpuUsageHealthCheck;
        private LowSensitivityHealthCheck _threadsCountHealthCheck;
        private LowSensitivityHealthCheck _orleansQueueHealthCheck;

        private readonly MetricsContext _context = Metric.Context("Workload");
        private Timer _triggerHealthChecksEvery5Seconds;
        private bool _disposed;

        private ILog Log { get; }


        public WorkloadMetricsLinux(Func<string, AggregatingHealthStatus> getAggregatingHealthStatus, Func<WorkloadMetricsConfig> getConfig, IDateTime dateTime, ILog log, PerformanceEventListener eventListener)
        {
            Log = log;
            _getConfig = getConfig;
            _dateTime = dateTime;
            _eventListener = eventListener;

            _healthStatus = getAggregatingHealthStatus("Workload");
        }

        
        public void Init()
        {
            _eventListener.Subscribe("Working Set");
            _eventListener.Subscribe("% Processor Time");
            _eventListener.Subscribe("# of current logical Threads");
            _eventListener.Subscribe("# Gen 1 Collections"); 
            _eventListener.Subscribe("# Gen 2 Collections");
            _eventListener.Subscribe("% Time in GC");
            _eventListener.Subscribe("# Bytes in all Heaps");
            _eventListener.Subscribe("Allocated Bytes/second");
            _eventListener.Subscribe("# of Exceps Thrown / Sec");
            _eventListener.Subscribe("gc-fragmentation");
            _eventListener.Subscribe("active-timer-count");


            _context.Context("CPU").Gauge("Processor Affinity", () => Process.GetCurrentProcess().ProcessorAffinityList().Count(), Unit.Items);
            _context.Context("CPU").Gauge("CPU usage", () => ReadPerfCounter("% Processor Time"), Unit.Percent);
            _context.Context("CPU").Gauge("Thread count", () => { double threads = ReadPerfCounter("# of current logical Threads"); return threads < 0 || threads > 1000000 ? 0 : threads; }, Unit.Items);

            _context.Context("Memory").Gauge("Working set", () => ReadPerfCounter("Working Set"), Unit.Bytes); 
            _context.Context("Memory").Gauge("# Bytes in all Heaps", () => ReadPerfCounter("# Bytes in all Heaps"), Unit.Bytes);
            _context.Context("Memory").Gauge("Allocated Bytes/second", () => ReadPerfCounter("Allocated Bytes/second"), Unit.Custom("Bytes/second"));

            _context.Context("GC").Gauge("Gen-1 collections", () => ReadPerfCounter("# Gen 1 Collections"), Unit.Events);
            _context.Context("GC").Gauge("Gen-2 collections", () => ReadPerfCounter("# Gen 2 Collections"), Unit.Events);
            _context.Context("GC").Gauge("Time in GC", () => ReadPerfCounter("% Time in GC"), Unit.Percent);
            _context.Context("GC").Gauge("GC Fragmentation", () => ReadPerfCounter("gc-fragmentation"), Unit.Percent);

            _context.Context("General").Gauge("# of Exceps Thrown / Sec", () => ReadPerfCounter("# of Exceps Thrown / Sec"), Unit.Items);
            _context.Context("General").Gauge("# of Active Timers", () => ReadPerfCounter("active-timer-count"), Unit.Items);


            _cpuUsageHealthCheck = new LowSensitivityHealthCheck(CpuUsageHealth, () => _getConfig().MinUnhealthyDuration, _dateTime);
            _threadsCountHealthCheck = new LowSensitivityHealthCheck(ThreadsCountHealth, () => _getConfig().MinUnhealthyDuration, _dateTime);
            _orleansQueueHealthCheck = new LowSensitivityHealthCheck(OrleansRequestQueueHealth, () => _getConfig().MinUnhealthyDuration, _dateTime);

            _healthStatus.RegisterCheck("CPU Usage", _cpuUsageHealthCheck.GetHealthStatus);
            _healthStatus.RegisterCheck("Threads Count", _threadsCountHealthCheck.GetHealthStatus);
            _healthStatus.RegisterCheck("Orleans Queue", _orleansQueueHealthCheck.GetHealthStatus);

            _triggerHealthChecksEvery5Seconds = new Timer(TriggerHealthCheck, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        private double ReadPerfCounter(string performanceCounterName)
        {
            if (_getConfig().ReadPerformanceCounters)
            {
                return _eventListener.ReadPerfCounter(performanceCounterName) ?? 0;
            }
            
            return 0;
        }

        private void TriggerHealthCheck(object state)
        {
            try
            {
                _cpuUsageHealthCheck.GetHealthStatus();
                _threadsCountHealthCheck.GetHealthStatus();
            }
            catch (Exception ex)
            {
                Log.Warn(x => x("Error triggering workload health status", exception: ex));
            }
        }

        private HealthCheckResult CpuUsageHealth()
        {
            if (!_getConfig().ReadPerformanceCounters)
                return HealthCheckResult.Healthy("CPU Usage: Reading perf counter disabled by configuration");

            var cpuUsage = ReadPerfCounter("% Processor Time");//ReadPerfCounter(_processorTimePercent);
            var maxCpuUsage = _getConfig().MaxHealthyCpuUsage;

            if (cpuUsage > maxCpuUsage)
                return HealthCheckResult.Unhealthy($"CPU Usage: {cpuUsage}% (unhealthy above {maxCpuUsage}%)");
            return HealthCheckResult.Healthy($"CPU Usage: {cpuUsage}% (unhealthy above {maxCpuUsage}%)");
        }


        private HealthCheckResult ThreadsCountHealth()
        {
            if (!_getConfig().ReadPerformanceCounters)
                return HealthCheckResult.Healthy("Threads: Reading perf counter disabled by configuration");

            var threads = ReadPerfCounter("# of current logical Threads");//ReadPerfCounter(_threadCount);
            var maxThreads = _getConfig().MaxHealthyThreadsCount;

            if (threads > maxThreads)
                return HealthCheckResult.Unhealthy($"Threads: {threads} (unhealthy above {maxThreads})");
            return HealthCheckResult.Healthy($"Threads: {threads} (unhealthy above {maxThreads})");
        }


        private HealthCheckResult OrleansRequestQueueHealth()
        {
            var queueLength = Metric.Context("Silo").DataProvider.CurrentMetricsData.Gauges
                                                    .FirstOrDefault(x => x.Name == "Request queue length")?.Value;
            if (queueLength == null)
                return HealthCheckResult.Healthy("Orleans queue length: unknown");

            var maxLength = _getConfig().MaxHealthyOrleansQueueLength;
            if (queueLength > maxLength)
                return HealthCheckResult.Unhealthy($"Orleans queue length: {queueLength} (unhealthy above {maxLength})");

            return HealthCheckResult.Healthy($"Orleans queue length: {queueLength} (unhealthy above {maxLength})");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _context?.Dispose();
            _triggerHealthChecksEvery5Seconds?.Dispose();
        }

    }

    /*
    public sealed class WorkloadMetricsLinux : IWorkloadMetrics
    {
        private readonly AggregatingHealthStatus _healthStatus;
        private readonly Func<WorkloadMetricsConfig> _getConfig;
        private readonly IDateTime _dateTime;

        readonly PerformanceCounterByProcess _virtualBytes = new PerformanceCounterByProcess("Process", "Virtual Bytes");
        readonly PerformanceCounterByProcess _privateBytes = new PerformanceCounterByProcess("Process", "Private Bytes");
        readonly PerformanceCounterByProcess _workingSet = new PerformanceCounterByProcess("Process", "Working Set");
        readonly PerformanceCounterByProcess _threadCount = new PerformanceCounterByProcess("Process", "Thread Count");
        readonly PerformanceCounterByProcess _dotNetThreadCount = new PerformanceCounterByProcess(".Net CLR LocksAndThreads", "# of current logical Threads");
        readonly PerformanceCounterByProcess _gen2Collections = new PerformanceCounterByProcess(".NET CLR Memory", "# Gen 2 Collections");
        readonly PerformanceCounterByProcess _timeInGc = new PerformanceCounterByProcess(".NET CLR Memory", "% Time in GC");
        readonly CpuUsageCounterByProcess _processorTimePercent;
        readonly CpuTotalAssignedCoresCounter _processorTotalPercent = new CpuTotalAssignedCoresCounter();

        private LowSensitivityHealthCheck _cpuUsageHealthCheck;
        private LowSensitivityHealthCheck _threadsCountHealthCheck;
        private LowSensitivityHealthCheck _orleansQueueHealthCheck;

        private readonly MetricsContext _context = Metric.Context("Workload");
        private Timer _triggerHealthChecksEvery5Seconds;
        private bool _disposed;

        private ILog Log { get; }

        public WorkloadMetricsLinux(Func<string, AggregatingHealthStatus> getAggregatingHealthStatus, Func<WorkloadMetricsConfig> getConfig, IDateTime dateTime, ILog log)
        {
            Log = log;
            _getConfig = getConfig;
            _dateTime = dateTime;
            _healthStatus = getAggregatingHealthStatus("Workload");
            _processorTimePercent = new CpuUsageCounterByProcess();
        }


        public void Init()
        {
            _context.Context("CPU").Gauge("Processor Affinity", () => _processorTimePercent.AssignedCoresCount, Unit.Items);
            _context.Context("CPU").Gauge("CPU usage", () => ReadPerfCounter(_processorTimePercent), Unit.Percent);
            _context.Context("CPU").Gauge("CPU total", () => _processorTotalPercent.GetValue() ?? 0, Unit.Percent);
            _context.Context("CPU").Gauge("Thread count", () => { double threads = ReadPerfCounter(_threadCount); return threads < 0 || threads > 1000000 ? 0 : threads; }, Unit.Items);
            _context.Context("CPU").Gauge("DotNet logical thread count", () => { double threads = ReadPerfCounter(_dotNetThreadCount); return threads < 0 || threads > 1000000 ? 0 : threads; }, Unit.Items);
            _context.Context("Memory").Gauge("Working set", () => ReadPerfCounter(_workingSet), Unit.Bytes);
            _context.Context("Memory").Gauge("Private", () => ReadPerfCounter(_virtualBytes), Unit.Bytes);
            _context.Context("Memory").Gauge("Virtual", () => ReadPerfCounter(_privateBytes), Unit.Bytes);
            _context.Context("GC").Gauge("Gen-2 collections", () => ReadPerfCounter(_gen2Collections), Unit.Events);
            _context.Context("GC").Gauge("Time in GC", () => ReadPerfCounter(_timeInGc), Unit.Percent);

            _cpuUsageHealthCheck = new LowSensitivityHealthCheck(CpuUsageHealth, () => _getConfig().MinUnhealthyDuration, _dateTime);
            _threadsCountHealthCheck = new LowSensitivityHealthCheck(ThreadsCountHealth, () => _getConfig().MinUnhealthyDuration, _dateTime);
            _orleansQueueHealthCheck = new LowSensitivityHealthCheck(OrleansRequestQueueHealth, () => _getConfig().MinUnhealthyDuration, _dateTime);

            _healthStatus.RegisterCheck("CPU Usage", _cpuUsageHealthCheck.GetHealthStatus);
            _healthStatus.RegisterCheck("Threads Count", _threadsCountHealthCheck.GetHealthStatus);
            _healthStatus.RegisterCheck("Orleans Queue", _orleansQueueHealthCheck.GetHealthStatus);

            _triggerHealthChecksEvery5Seconds = new Timer(TriggerHealthCheck, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        private void TriggerHealthCheck(object state)
        {
            try
            {
                _cpuUsageHealthCheck.GetHealthStatus();
                _threadsCountHealthCheck.GetHealthStatus();
            }
            catch (Exception ex)
            {
                Log.Warn(x => x("Error triggering workload health status", exception: ex));
            }
        }


        private double ReadPerfCounter(PerformanceCounterByProcess perfCounter)
        {
            if (_getConfig().ReadPerformanceCounters)
            {
                return perfCounter.GetValue() ?? 0;
            }
            else
                return 0;
        }

        private HealthCheckResult CpuUsageHealth()
        {
            if (!_getConfig().ReadPerformanceCounters)
                return HealthCheckResult.Healthy("CPU Usage: Reading perf counter disabled by configuration");

            var cpuUsage = ReadPerfCounter(_processorTimePercent);
            var maxCpuUsage = _getConfig().MaxHealthyCpuUsage;

            if (cpuUsage > maxCpuUsage)
                return HealthCheckResult.Unhealthy($"CPU Usage: {cpuUsage}% (unhealthy above {maxCpuUsage}%)");
            else
                return HealthCheckResult.Healthy($"CPU Usage: {cpuUsage}% (unhealthy above {maxCpuUsage}%)");
        }


        private HealthCheckResult ThreadsCountHealth()
        {
            if (!_getConfig().ReadPerformanceCounters)
                return HealthCheckResult.Healthy("Threads: Reading perf counter disabled by configuration");

            var threads = ReadPerfCounter(_threadCount);
            var maxThreads = _getConfig().MaxHealthyThreadsCount;

            if (threads > maxThreads)
                return HealthCheckResult.Unhealthy($"Threads: {threads} (unhealthy above {maxThreads})");
            else
                return HealthCheckResult.Healthy($"Threads: {threads} (unhealthy above {maxThreads})");
        }


        private HealthCheckResult OrleansRequestQueueHealth()
        {
            var queueLength = Metric.Context("Silo").DataProvider.CurrentMetricsData.Gauges
                                                    .FirstOrDefault(x => x.Name == "Request queue length")?.Value;
            if (queueLength == null)
                return HealthCheckResult.Healthy("Orleans queue length: unknown");

            var maxLength = _getConfig().MaxHealthyOrleansQueueLength;
            if (queueLength > maxLength)
                return HealthCheckResult.Unhealthy($"Orleans queue length: {queueLength} (unhealthy above {maxLength})");

            return HealthCheckResult.Healthy($"Orleans queue length: {queueLength} (unhealthy above {maxLength})");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _context?.Dispose();
            _triggerHealthChecksEvery5Seconds?.Dispose();

            //_processorTimePercent?.Dispose();
            //_processorTotalPercent?.Dispose();
            //_virtualBytes?.Dispose();
            //_privateBytes?.Dispose();
            //_workingSet?.Dispose();
            //_threadCount?.Dispose();
            //_dotNetThreadCount?.Dispose();
            //_gen2Collections?.Dispose();
            //_timeInGc?.Dispose();
        }
    }
    */
}
