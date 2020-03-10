// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.Common.Telemetry
{
    /// <summary>
    /// The Telemetry data constants.
    /// </summary>
    public static class TelemetryDataConstants
    {
        // ******************** Execution ***********************
        public const string ParallelEnabledDuringExecution = "VS.TestRun.ParallelEnabled";

        // Total number of tests ran under one test request
        public const string TotalTestsRun = "VS.TestRun.TotalTests";

        // Total time taken to complete one test run request
        public const string TimeTakenInSecForRun = "VS.TestRun.TimeTakenInSec";

        public const string TestSettingsUsed = "VS.TestRun.IsTestSettingsUsed";

        public const string DisableAppDomain = "VS.TestRun.DisableAppDomain";

        // All data related to legacy settings nodes will be prefixed with this.
        public const string LegacySettingPrefix = "VS.TestRun.LegacySettings";

        public const string DataCollectorsEnabled = "VS.TestRun.DataCollectorsEnabled";

        public const string RunState = "VS.TestRun.RunState";

        public const string NumberOfSourcesSentForRun = "VS.TestRun.NumberOfSources";

        public const string TargetDevice = "VS.TestRun.TargetDevice";

        public const string TargetFramework = "VS.TestRun.TargetFramework";

        public const string TargetPlatform = "VS.TestRun.TargetPlatform";

        public const string MaxCPUcount = "VS.TestRun.MaxCPUcount";

        public const string TestPlatformVersion = "VS.TestRun.TestPlatformVersion";

        public const string TargetOS = "VS.TestRun.TargetOS";

        public const string LoggerUsed = "VS.TestRun.LoggersUsed";

        public const string CommandLineSwitches = "VS.TestRun.CommandLineSwitches";

        // Adapter name will get appended. eg:- VS.TestRun.TotalTestsRun.executor//cppunittestexecutor/v1
        // In case of parallel it will be sum of all tests ran by an adapter in different execution process
        public const string TotalTestsRanByAdapter = "VS.TestRun.TotalTestsRun";

        // Adapter name will get appended. eg:- VS.TestRun.TimeTakenToRun.executor//cppunittestexecutor/v1
        // In case of parallel it will be sum of all time taken by an adapter to run tests in different execution process
        public const string TimeTakenToRunTestsByAnAdapter = "VS.TestRun.TimeTakenToRun";

        // Total number of adapter discovered on the machine.
        public const string NumberOfAdapterDiscoveredDuringExecution = "VS.TestRun.AdaptersDiscoveredCount";

        public const string NumberOfAdapterUsedToRunTests = "VS.TestRun.AdaptersUsedCount";

        // It will be the sum of the times taken by all adapter to run tests.
        // In case of parallel it can be more than total time taken to complete run request.
        public const string TimeTakenByAllAdaptersInSec = "VS.TestRun.TimeTakenByAllAdapters";

        // *********************Discovery****************************
        public const string TotalTestsDiscovered = "VS.TestDiscovery.TotalTests";

        public const string ParallelEnabledDuringDiscovery = "VS.TestDiscovery.ParallelEnabled";

        // All the times are in sec
        public const string TimeTakenInSecForDiscovery = "VS.TestDiscovery.TotalTimeTakenInSec";

        public const string TimeTakenToLoadAdaptersInSec = "VS.TestDiscovery.TimeTakenToLoadAdaptersInSec";

        // It will be the sum of the times taken by all adapter to discover tests.
        public const string TimeTakenInSecByAllAdapters = "VS.TestDiscovery.TimeTakenInSecByAllAdapters";

        // Adapter name will get appended. eg:- VS.TestDiscovery.TimeTakenAdapter.executor//cppunittestexecutor/v1
        public const string TimeTakenToDiscoverTestsByAnAdapter = "VS.TestDiscovery.TimeTakenAdapter";

        // Adapter name will get appended. eg:- VS.TestDiscovery.TotalTestsDiscovered.executor//cppunittestexecutor/v1
        public const string TotalTestsByAdapter = "VS.TestDiscovery.TotalTestsDiscovered";

        public const string DiscoveryState = "VS.TestDiscovery.DiscoveryState";

        public const string NumberOfSourcesSentForDiscovery = "VS.TestDiscovery.NumberOfSources";

        public const string NumberOfAdapterDiscoveredDuringDiscovery = "VS.TestDiscovery.AdaptersDiscoveredCount";

        public const string NumberOfAdapterUsedToDiscoverTests = "VS.TestDiscovery.AdaptersUsedCount";

        // **************Events Name **********************************
        public const string TestDiscoveryCompleteEvent = "vs/testplatform/testdiscoverysession";

        public const string TestExecutionCompleteEvent = "vs/testplatform/testrunsession";
    }
}
