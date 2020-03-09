// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.CommandLine.TestPlatformHelpers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using System.Xml;
    using System.Xml.XPath;
    using Microsoft.VisualStudio.TestPlatform.Client;
    using Microsoft.VisualStudio.TestPlatform.Client.RequestHelper;
    using Microsoft.VisualStudio.TestPlatform.CommandLine.Internal;
    using Microsoft.VisualStudio.TestPlatform.CommandLine.Processors.Utilities;
    using Microsoft.VisualStudio.TestPlatform.CommandLine.Publisher;
    using Microsoft.VisualStudio.TestPlatform.CommandLine.Resources;
    using Microsoft.VisualStudio.TestPlatform.CommandLineUtilities;
    using Microsoft.VisualStudio.TestPlatform.Common;
    using Microsoft.VisualStudio.TestPlatform.Common.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.Common.Telemetry;
    using Microsoft.VisualStudio.TestPlatform.Common.Utilities;
    using Microsoft.VisualStudio.TestPlatform.CoreUtilities.Tracing;
    using Microsoft.VisualStudio.TestPlatform.CoreUtilities.Tracing.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;
    using Microsoft.VisualStudio.TestPlatform.PlatformAbstractions;
    using Microsoft.VisualStudio.TestPlatform.PlatformAbstractions.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.Utilities;

    /// <summary>
    /// Defines the TestRequestManger which can fire off discovery and test run requests
    /// </summary>
    internal class TestRequestManager : ITestRequestManager
    {
        private readonly ITestPlatform testPlatform;
        private CommandLineOptions commandLineOptions;
        private readonly ITestPlatformEventSource testPlatformEventSource;
        private TestRunResultAggregator testRunResultAggregator;
        private static ITestRequestManager testRequestManagerInstance;
        private InferHelper inferHelper;
        private const int runRequestTimeout = 5000;
        private bool telemetryOptedIn;
        private readonly object syncObject = new object();
        private readonly Task<IMetricsPublisher> metricsPublisher;
        private bool isDisposed;
        private IProcessHelper processHelper;

        /// <summary>
        /// Maintains the current active execution request
        /// Assumption : There can only be one active execution request.
        /// </summary>
        private ITestRunRequest currentTestRunRequest;

        /// <summary>
        /// Maintains the current active discovery request
        /// Assumption : There can only be one active discovery request.
        /// </summary>
        private IDiscoveryRequest currentDiscoveryRequest;

        #region Constructor

        public TestRequestManager()
            : this(
                  CommandLineOptions.Instance,
                  TestPlatformFactory.GetTestPlatform(),
                  TestRunResultAggregator.Instance,
                  TestPlatformEventSource.Instance,
                  new InferHelper(AssemblyMetadataProvider.Instance),
                  MetricsPublisherFactory.GetMetricsPublisher(IsTelemetryOptedIn(), CommandLineOptions.Instance.IsDesignMode),
                  new ProcessHelper())
        {
        }

        internal TestRequestManager(CommandLineOptions commandLineOptions, ITestPlatform testPlatform, TestRunResultAggregator testRunResultAggregator, ITestPlatformEventSource testPlatformEventSource, InferHelper inferHelper, Task<IMetricsPublisher> metricsPublisher, IProcessHelper processHelper)
        {
            this.testPlatform = testPlatform;
            this.commandLineOptions = commandLineOptions;
            this.testRunResultAggregator = testRunResultAggregator;
            this.testPlatformEventSource = testPlatformEventSource;
            this.inferHelper = inferHelper;
            this.metricsPublisher = metricsPublisher;
            this.processHelper = processHelper;
        }

        #endregion

        public static ITestRequestManager Instance
        {
            get
            {
                if (testRequestManagerInstance == null)
                {
                    testRequestManagerInstance = new TestRequestManager();
                }

                return testRequestManagerInstance;
            }
        }

        #region ITestRequestManager

        /// <inheritdoc />
        public void InitializeExtensions(IEnumerable<string> pathToAdditionalExtensions, bool skipExtensionFilters)
        {
            // It is possible for an Editor/IDE to keep running the runner in design mode for long duration.
            // We clear the extensions cache to ensure the extensions don't get reused across discovery/run
            // requests.
            EqtTrace.Info("TestRequestManager.InitializeExtensions: Initialize extensions started.");
            this.testPlatform.ClearExtensions();
            this.testPlatform.UpdateExtensions(pathToAdditionalExtensions, skipExtensionFilters);
            EqtTrace.Info("TestRequestManager.InitializeExtensions: Initialize extensions completed.");
        }

        /// <summary>
        /// Resets the command options
        /// </summary>
        public void ResetOptions()
        {
            this.commandLineOptions.Reset();
        }

        /// <summary>
        /// Discover Tests given a list of sources, run settings.
        /// </summary>
        /// <param name="discoveryPayload">Discovery payload</param>
        /// <param name="discoveryEventsRegistrar">EventHandler for discovered tests</param>
        /// <param name="protocolConfig">Protocol related information</param>
        /// <returns>True, if successful</returns>
        public void DiscoverTests(DiscoveryRequestPayload discoveryPayload, ITestDiscoveryEventsRegistrar discoveryEventsRegistrar, ProtocolConfig protocolConfig)
        {
            EqtTrace.Info("TestRequestManager.DiscoverTests: Discovery tests started.");

            var runsettings = discoveryPayload.RunSettings;

            if (discoveryPayload.TestPlatformOptions != null)
            {
                this.telemetryOptedIn = discoveryPayload.TestPlatformOptions.CollectMetrics;
            }

            var requestData = this.GetRequestData(protocolConfig);
            if (this.UpdateRunSettingsIfRequired(runsettings, discoveryPayload.Sources?.ToList(), discoveryEventsRegistrar, out string updatedRunsettings))
            {
                runsettings = updatedRunsettings;
            }

            var runConfiguration = XmlRunSettingsUtilities.GetRunConfigurationNode(runsettings);
            var batchSize = runConfiguration.BatchSize;

            if (requestData.IsTelemetryOptedIn)
            {
                // Collect Metrics
                this.CollectMetrics(requestData, runConfiguration);

                // Collect Commands
                this.LogCommandsTelemetryPoints(requestData);
            }

            // create discovery request
            var criteria = new DiscoveryCriteria(discoveryPayload.Sources, batchSize, this.commandLineOptions.TestStatsEventTimeout, runsettings)
            {
                TestCaseFilter = this.commandLineOptions.TestCaseFilterValue
            };

            // Make sure to run the run request inside a lock as the below section is not thread-safe
            // There can be only one discovery or execution request at a given point in time
            lock (this.syncObject)
            {
                try
                {
                    EqtTrace.Info("TestRequestManager.DiscoverTests: Synchronization context taken");

                    this.currentDiscoveryRequest = this.testPlatform.CreateDiscoveryRequest(requestData, criteria, discoveryPayload.TestPlatformOptions);
                    discoveryEventsRegistrar?.RegisterDiscoveryEvents(this.currentDiscoveryRequest);

                    // Notify start of discovery start
                    this.testPlatformEventSource.DiscoveryRequestStart();

                    // Start the discovery of tests and wait for completion
                    this.currentDiscoveryRequest.DiscoverAsync();
                    this.currentDiscoveryRequest.WaitForCompletion();
                }
                finally
                {
                    if (this.currentDiscoveryRequest != null)
                    {
                        // Dispose the discovery request and unregister for events
                        discoveryEventsRegistrar?.UnregisterDiscoveryEvents(currentDiscoveryRequest);
                        this.currentDiscoveryRequest.Dispose();
                        this.currentDiscoveryRequest = null;
                    }

                    EqtTrace.Info("TestRequestManager.DiscoverTests: Discovery tests completed.");
                    this.testPlatformEventSource.DiscoveryRequestStop();

                    // Posts the Discovery Complete event.
                    this.metricsPublisher.Result.PublishMetrics(TelemetryDataConstants.TestDiscoveryCompleteEvent, requestData.MetricsCollection.Metrics);
                }
            }
        }

        /// <summary>
        /// Run Tests with given a set of test cases.
        /// </summary>
        /// <param name="testRunRequestPayload">TestRun request Payload</param>
        /// <param name="testHostLauncher">TestHost Launcher for the run</param>
        /// <param name="testRunEventsRegistrar">event registrar for run events</param>
        /// <param name="protocolConfig">Protocol related information</param>
        /// <returns>True, if successful</returns>
        public void RunTests(TestRunRequestPayload testRunRequestPayload, ITestHostLauncher testHostLauncher, ITestRunEventsRegistrar testRunEventsRegistrar, ProtocolConfig protocolConfig)
        {
            EqtTrace.Info("TestRequestManager.RunTests: run tests started.");

            TestRunCriteria runCriteria = null;
            var runsettings = testRunRequestPayload.RunSettings;

            if (testRunRequestPayload.TestPlatformOptions != null)
            {
                this.telemetryOptedIn = testRunRequestPayload.TestPlatformOptions.CollectMetrics;
            }

            var requestData = this.GetRequestData(protocolConfig);

            // Get sources to auto detect fx and arch for both run selected or run all scenario.
            var sources = GetSources(testRunRequestPayload);

            if (this.UpdateRunSettingsIfRequired(runsettings, sources, testRunEventsRegistrar, out string updatedRunsettings))
            {
                runsettings = updatedRunsettings;
            }

            if (InferRunSettingsHelper.AreRunSettingsCollectorsInCompatibleWithTestSettings(runsettings))
            {
                throw new SettingsException(string.Format(Resources.RunsettingsWithDCErrorMessage, runsettings));
            }

            var runConfiguration = XmlRunSettingsUtilities.GetRunConfigurationNode(runsettings);
            var batchSize = runConfiguration.BatchSize;

            if (requestData.IsTelemetryOptedIn)
            {
                // Collect Metrics
                this.CollectMetrics(requestData, runConfiguration);

                // Collect Commands
                this.LogCommandsTelemetryPoints(requestData);

                // Collect data for Legacy Settings
                this.LogTelemetryForLegacySettings(requestData, runsettings);
            }

            if (!commandLineOptions.IsDesignMode)
            {
                // Generate fakes settings only for command line scenarios. In case of
                // Editors/IDEs, this responsibility is with the caller.
                GenerateFakesUtilities.GenerateFakesSettings(this.commandLineOptions, this.commandLineOptions.Sources.ToList(), ref runsettings);
            }

            if (testRunRequestPayload.Sources != null && testRunRequestPayload.Sources.Any())
            {
                runCriteria = new TestRunCriteria(
                                  testRunRequestPayload.Sources,
                                  batchSize,
                                  testRunRequestPayload.KeepAlive,
                                  runsettings,
                                  this.commandLineOptions.TestStatsEventTimeout,
                                  testHostLauncher,
                                  testRunRequestPayload.TestPlatformOptions?.TestCaseFilter,
                                  testRunRequestPayload.TestPlatformOptions?.FilterOptions);
            }
            else
            {
                runCriteria = new TestRunCriteria(
                                  testRunRequestPayload.TestCases,
                                  batchSize,
                                  testRunRequestPayload.KeepAlive,
                                  runsettings,
                                  this.commandLineOptions.TestStatsEventTimeout,
                                  testHostLauncher);
            }

            // Run tests
            try
            {
                this.RunTests(requestData, runCriteria, testRunEventsRegistrar, testRunRequestPayload.TestPlatformOptions);
                EqtTrace.Info("TestRequestManager.RunTests: run tests completed.");
            }
            finally
            {
                this.testPlatformEventSource.ExecutionRequestStop();

                // Post the run complete event
                this.metricsPublisher.Result.PublishMetrics(TelemetryDataConstants.TestExecutionCompleteEvent, requestData.MetricsCollection.Metrics);
            }
        }

        private void LogTelemetryForLegacySettings(IRequestData requestData, string runsettings)
        {
            requestData.MetricsCollection.Add(TelemetryDataConstants.TestSettingsUsed, InferRunSettingsHelper.IsTestSettingsEnabled(runsettings));

            if (InferRunSettingsHelper.TryGetLegacySettingElements(runsettings, out Dictionary<string, string> legacySettingsTelemetry))
            {
                foreach( var ciData in legacySettingsTelemetry)
                {
                    // We are collecting telemetry for the legacy nodes and attributes used in the runsettings.
                    requestData.MetricsCollection.Add(string.Format("{0}.{1}", TelemetryDataConstants.LegacySettingPrefix, ciData.Key), ciData.Value);
                }
            }
        }

        /// <summary>
        /// Cancel the test run.
        /// </summary>
        public void CancelTestRun()
        {
            EqtTrace.Info("TestRequestManager.CancelTestRun: Sending cancel request.");
            this.currentTestRunRequest?.CancelAsync();
        }

        /// <summary>
        /// Cancel the test discovery.
        /// </summary>
        public void CancelDiscovery()
        {
            EqtTrace.Info("TestRequestManager.CancelTestDiscovery: Sending cancel request.");
            this.currentDiscoveryRequest?.Abort();
        }

        /// <summary>
        /// Aborts the test run.
        /// </summary>
        public void AbortTestRun()
        {
            EqtTrace.Info("TestRequestManager.AbortTestRun: Sending abort request.");
            this.currentTestRunRequest?.Abort();
        }

        #endregion

        public void Dispose()
        {
            this.Dispose(true);

            // Use SupressFinalize in case a subclass
            // of this type implements a finalizer.
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!this.isDisposed)
            {
                if (disposing)
                {
                    this.metricsPublisher.Result.Dispose();
                }

                this.isDisposed = true;
            }
        }

        private bool UpdateRunSettingsIfRequired(string runsettingsXml, List<string> sources, IBaseTestEventsRegistrar registrar, out string updatedRunSettingsXml)
        {
            bool settingsUpdated = false;
            updatedRunSettingsXml = runsettingsXml;
            IDictionary<string, Architecture> sourcePlatforms = new Dictionary<string, Architecture>();
            IDictionary<string, Framework> sourceFrameworks = new Dictionary<string, Framework>();

            if (!string.IsNullOrEmpty(runsettingsXml))
            {
                // TargetFramework is full CLR. Set DesignMode based on current context.
                using (var stream = new StringReader(runsettingsXml))
                using (var reader = XmlReader.Create(stream, XmlRunSettingsUtilities.ReaderSettings))
                {
                    var document = new XmlDocument();
                    document.Load(reader);
                    var navigator = document.CreateNavigator();
                    var runConfiguration = XmlRunSettingsUtilities.GetRunConfigurationNode(runsettingsXml);
                    var loggerRunSettings = XmlRunSettingsUtilities.GetLoggerRunSettings(runsettingsXml) ?? new LoggerRunSettings();

                    settingsUpdated |= this.UpdateFramework(document, navigator, sources, sourceFrameworks, registrar, out Framework chosenFramework);

                    // Choose default architecture based on the framework
                    // For .NET core, the default platform architecture should be based on the process.
                    // For a 64 bit process,
                    Architecture defaultArchitecture = Architecture.X86;
                    if (chosenFramework.Name.IndexOf("netstandard", StringComparison.OrdinalIgnoreCase) >= 0
                    || chosenFramework.Name.IndexOf("netcoreapp", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var currentProcessName = this.processHelper.GetProcessName(this.processHelper.GetCurrentProcessId());
                        defaultArchitecture = (currentProcessName.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) && !Environment.Is64BitProcess) ? Architecture.X86: Architecture.X64;
                    }

                    settingsUpdated |= this.UpdatePlatform(document, navigator, sources, sourcePlatforms, defaultArchitecture, out Architecture chosenPlatform);
                    CheckSourcesForCompatibility(chosenFramework, chosenPlatform, sourcePlatforms, sourceFrameworks, registrar);
                    settingsUpdated |= this.UpdateDesignMode(document, runConfiguration);
                    settingsUpdated |= this.UpdateCollectSourceInformation(document, runConfiguration);
                    settingsUpdated |= UpdateTargetDevice(navigator, document, runConfiguration);
                    settingsUpdated |= this.AddOrUpdateConsoleLogger(document, runConfiguration, loggerRunSettings);

                    updatedRunSettingsXml = navigator.OuterXml;
                }
            }

            return settingsUpdated;
        }

        private bool AddOrUpdateConsoleLogger(XmlDocument document, RunConfiguration runConfiguration, LoggerRunSettings loggerRunSettings)
        {
            // Update console logger settings
            bool consoleLoggerUpdated = UpdateConsoleLoggerIfExists(document, loggerRunSettings);

            // In case of CLI, add console logger if not already present.
            bool designMode = runConfiguration.DesignModeSet ? runConfiguration.DesignMode : this.commandLineOptions.IsDesignMode;
            if (!designMode && !consoleLoggerUpdated)
            {
                AddConsoleLogger(document, loggerRunSettings);
            }

            // Update is required 1) in case of CLI 2) in case of design mode if console logger is present in runsettings.
            return !designMode || consoleLoggerUpdated;
        }

        private static bool UpdateTargetDevice(XPathNavigator navigator, XmlDocument document, RunConfiguration runConfiguration)
        {
            bool updateRequired = InferRunSettingsHelper.TryGetDeviceXml(navigator, out string deviceXml);
            if (updateRequired)
            {
                InferRunSettingsHelper.UpdateTargetDevice(document, deviceXml);
            }
            return updateRequired;
        }

        private bool UpdateCollectSourceInformation(XmlDocument document, RunConfiguration runConfiguration)
        {
            bool updateRequired = !runConfiguration.CollectSourceInformationSet;
            if (updateRequired)
            {
                InferRunSettingsHelper.UpdateCollectSourceInformation(document, this.commandLineOptions.ShouldCollectSourceInformation);
            }
            return updateRequired;
        }

        private bool UpdateDesignMode(XmlDocument document, RunConfiguration runConfiguration)
        {
            // If user is already setting DesignMode via runsettings or CLI args; we skip.
            bool updateRequired = !runConfiguration.DesignModeSet;
            if (updateRequired)
            {
                InferRunSettingsHelper.UpdateDesignMode(document, this.commandLineOptions.IsDesignMode);
            }
            return updateRequired;
        }

        private static void CheckSourcesForCompatibility(Framework chosenFramework, Architecture chosenPlatform, IDictionary<string, Architecture> sourcePlatforms, IDictionary<string, Framework> sourceFrameworks, IBaseTestEventsRegistrar registrar)
        {
            // Find compatible sources
            var compatibleSources = InferRunSettingsHelper.FilterCompatibleSources(chosenPlatform, chosenFramework, sourcePlatforms, sourceFrameworks, out var incompatibleSettingWarning);

            // Raise warnings for incompatible sources
            if (!string.IsNullOrEmpty(incompatibleSettingWarning))
            {
                EqtTrace.Warning(incompatibleSettingWarning);
                registrar?.LogWarning(incompatibleSettingWarning);
            }

            // Log compatible sources
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("Compatible sources list : ");
                EqtTrace.Info(string.Join("\n", compatibleSources.ToArray()));
            }
        }

        private bool UpdatePlatform(XmlDocument document, XPathNavigator navigator, List<string> sources, IDictionary<string, Architecture> sourcePlatforms, Architecture defaultArchitecture, out Architecture chosenPlatform)
        {
            // Get platform from sources
            var inferedPlatform = inferHelper.AutoDetectArchitecture(sources, sourcePlatforms, defaultArchitecture);

            // Get platform from runsettings
            bool updatePlatform = IsAutoPlatformDetectRequired(navigator, out chosenPlatform);

            // Update platform if required. For command line scenario update happens in ArgumentProcessor.
            if (updatePlatform)
            {
                InferRunSettingsHelper.UpdateTargetPlatform(document, inferedPlatform.ToString(), overwrite: true);
                chosenPlatform = inferedPlatform;
            }

            return updatePlatform;
        }

        private bool UpdateFramework(XmlDocument document, XPathNavigator navigator, List<string> sources, IDictionary<string, Framework> sourceFrameworks, IBaseTestEventsRegistrar registrar, out Framework chosenFramework)
        {
            // Get framework from sources
            var inferedFramework = inferHelper.AutoDetectFramework(sources, sourceFrameworks);

            // Get framework from runsettings.
            bool updateFramework = IsAutoFrameworkDetectRequired(navigator, out chosenFramework);

            // Update framework if required. For command line scenario update happens in ArgumentProcessor.
            if (updateFramework)
            {
                InferRunSettingsHelper.UpdateTargetFramework(document, inferedFramework?.ToString(), overwrite: true);
                chosenFramework = inferedFramework;
            }

            // Raise warnings for unsupported frameworks.
            if (Constants.DotNetFramework35.Equals(chosenFramework.Name))
            {
                EqtTrace.Warning("TestRequestManager.UpdateRunSettingsIfRequired: throw warning on /Framework:Framework35 option.");
                registrar.LogWarning(Resources.Framework35NotSupported);
            }

            return updateFramework;
        }

        /// <summary>
        /// Add console logger in runsettings.
        /// </summary>
        /// <param name="document">Runsettings document.</param>
        /// <param name="loggerRunSettings">Logger run settings.</param>
        private static void AddConsoleLogger(XmlDocument document, LoggerRunSettings loggerRunSettings)
        {
            var consoleLogger = new LoggerSettings
            {
                FriendlyName = ConsoleLogger.FriendlyName,
                Uri = new Uri(ConsoleLogger.ExtensionUri),
                AssemblyQualifiedName = typeof(ConsoleLogger).AssemblyQualifiedName,
                CodeBase = typeof(ConsoleLogger).GetTypeInfo().Assembly.Location,
                IsEnabled = true
            };

            loggerRunSettings.LoggerSettingsList.Add(consoleLogger);
            RunSettingsProviderExtensions.UpdateRunSettingsXmlDocumentInnerXml(document, Constants.LoggerRunSettingsName, loggerRunSettings.ToXml().InnerXml);
        }

        /// <summary>
        /// Add console logger in runsettings if exists.
        /// </summary>
        /// <param name="document">Runsettings document.</param>
        /// <param name="loggerRunSettings">Logger run settings.</param>
        /// <returns>True if updated console logger in runsettings successfully.</returns>
        private static bool UpdateConsoleLoggerIfExists(XmlDocument document, LoggerRunSettings loggerRunSettings)
        {
            var defaultConsoleLogger = new LoggerSettings
            {
                FriendlyName = ConsoleLogger.FriendlyName,
                Uri = new Uri(ConsoleLogger.ExtensionUri)
            };

            var existingLoggerIndex = loggerRunSettings.GetExistingLoggerIndex(defaultConsoleLogger);

            // Update assemblyQualifiedName and codeBase of existing logger.
            if (existingLoggerIndex >= 0)
            {
                var consoleLogger = loggerRunSettings.LoggerSettingsList[existingLoggerIndex];
                consoleLogger.AssemblyQualifiedName = typeof(ConsoleLogger).AssemblyQualifiedName;
                consoleLogger.CodeBase = typeof(ConsoleLogger).GetTypeInfo().Assembly.Location;
                RunSettingsProviderExtensions.UpdateRunSettingsXmlDocumentInnerXml(document, Constants.LoggerRunSettingsName, loggerRunSettings.ToXml().InnerXml);
                return true;
            }

            return false;
        }

        private void RunTests(IRequestData requestData, TestRunCriteria testRunCriteria, ITestRunEventsRegistrar testRunEventsRegistrar, TestPlatformOptions options)
        {
            // Make sure to run the run request inside a lock as the below section is not thread-safe
            // TranslationLayer can process faster as it directly gets the raw un-serialized messages whereas 
            // below logic needs to deserialize and do some cleanup
            // While this section is cleaning up, TranslationLayer can trigger run causing multiple threads to run the below section at the same time
            lock (this.syncObject)
            {
                try
                {
                    this.currentTestRunRequest = this.testPlatform.CreateTestRunRequest(requestData, testRunCriteria, options);

                    this.testRunResultAggregator.RegisterTestRunEvents(this.currentTestRunRequest);
                    testRunEventsRegistrar?.RegisterTestRunEvents(this.currentTestRunRequest);

                    this.testPlatformEventSource.ExecutionRequestStart();

                    this.currentTestRunRequest.ExecuteAsync();

                    // Wait for the run completion event
                    this.currentTestRunRequest.WaitForCompletion();
                }
                catch (Exception ex)
                {
                    EqtTrace.Error("TestRequestManager.RunTests: failed to run tests: {0}", ex);
                    testRunResultAggregator.MarkTestRunFailed();
                    throw;
                }
                finally
                {
                    if (this.currentTestRunRequest != null)
                    {
                        this.testRunResultAggregator.UnregisterTestRunEvents(this.currentTestRunRequest);
                        testRunEventsRegistrar?.UnregisterTestRunEvents(this.currentTestRunRequest);

                        this.currentTestRunRequest.Dispose();
                        this.currentTestRunRequest = null;
                    }
                }
            }
        }

        private bool IsAutoFrameworkDetectRequired(XPathNavigator navigator, out Framework chosenFramework)
        {
            bool required = true;
            chosenFramework = null;
            if (commandLineOptions.IsDesignMode)
            {
                bool isValidFx =
                    InferRunSettingsHelper.TryGetFrameworkXml(navigator, out var frameworkFromrunsettingsXml);
                required = !isValidFx || string.IsNullOrWhiteSpace(frameworkFromrunsettingsXml);
                if (!required)
                {
                    chosenFramework = Framework.FromString(frameworkFromrunsettingsXml);
                }
            }
            else if (!commandLineOptions.IsDesignMode && commandLineOptions.FrameworkVersionSpecified)
            {
                required = false;
                chosenFramework = commandLineOptions.TargetFrameworkVersion;
            }

            return required;
        }

        private bool IsAutoPlatformDetectRequired(XPathNavigator navigator, out Architecture chosenPlatform)
        {
            bool required = true;
            chosenPlatform = Architecture.Default;
            if (commandLineOptions.IsDesignMode)
            {
                bool isValidPlatform = InferRunSettingsHelper.TryGetPlatformXml(navigator, out var platformXml);
                required = !isValidPlatform || string.IsNullOrWhiteSpace(platformXml);
                if (!required)
                {
                    chosenPlatform = (Architecture)Enum.Parse(typeof(Architecture), platformXml, true);
                }
            }
            else if (!commandLineOptions.IsDesignMode && commandLineOptions.ArchitectureSpecified)
            {
                required = false;
                chosenPlatform = commandLineOptions.TargetArchitecture;
            }

            return required;
        }

        /// <summary>
        /// Collect Metrics
        /// </summary>
        /// <param name="requestData">Request Data for common Discovery/Execution Services</param>
        /// <param name="runConfiguration">RunConfiguration</param>
        private void CollectMetrics(IRequestData requestData, RunConfiguration runConfiguration)
        {
            // Collecting Target Framework.
            requestData.MetricsCollection.Add(TelemetryDataConstants.TargetFramework, runConfiguration.TargetFramework.Name);

            // Collecting Target Platform.
            requestData.MetricsCollection.Add(TelemetryDataConstants.TargetPlatform, runConfiguration.TargetPlatform.ToString());

            // Collecting Max CPU count.
            requestData.MetricsCollection.Add(TelemetryDataConstants.MaxCPUcount, runConfiguration.MaxCpuCount);

            // Collecting Target Device. Here, it will be updated run settings so, target device will be under run configuration only.
            var targetDevice = runConfiguration.TargetDevice;
            if (string.IsNullOrEmpty(targetDevice))
            {
                requestData.MetricsCollection.Add(TelemetryDataConstants.TargetDevice, "Local Machine");
            }
            else if (targetDevice.Equals("Device", StringComparison.Ordinal) || targetDevice.Contains("Emulator"))
            {
                requestData.MetricsCollection.Add(TelemetryDataConstants.TargetDevice, targetDevice);
            }
            else
            {
                // For IOT scenarios
                requestData.MetricsCollection.Add(TelemetryDataConstants.TargetDevice, "Other");
            }

            // Collecting TestPlatform Version
            requestData.MetricsCollection.Add(TelemetryDataConstants.TestPlatformVersion, Product.Version);

            // Collecting TargetOS
            requestData.MetricsCollection.Add(TelemetryDataConstants.TargetOS, new PlatformEnvironment().OperatingSystemVersion);

            //Collecting DisableAppDomain
            requestData.MetricsCollection.Add(TelemetryDataConstants.DisableAppDomain, runConfiguration.DisableAppDomain);

        }

        /// <summary>
        /// Checks whether Telemetry opted in or not.
        /// By Default opting out
        /// </summary>
        /// <returns>Returns Telemetry Opted out or not</returns>
        private static bool IsTelemetryOptedIn()
        {
            var telemetryStatus = Environment.GetEnvironmentVariable("VSTEST_TELEMETRY_OPTEDIN");
            return !string.IsNullOrEmpty(telemetryStatus) && telemetryStatus.Equals("1", StringComparison.Ordinal);
        }

        /// <summary>
        /// Log Command Line switches for Telemetry purposes
        /// </summary>
        /// <param name="requestData">Request Data providing common discovery/execution services.</param>
        private void LogCommandsTelemetryPoints(IRequestData requestData)
        {
            var commandsUsed = new List<string>();

            var parallel = this.commandLineOptions.Parallel;
            if (parallel)
            {
                commandsUsed.Add("/Parallel");
            }

            var platform = this.commandLineOptions.ArchitectureSpecified;
            if (platform)
            {
                commandsUsed.Add("/Platform");
            }

            var enableCodeCoverage = this.commandLineOptions.EnableCodeCoverage;
            if (enableCodeCoverage)
            {
                commandsUsed.Add("/EnableCodeCoverage");
            }

            var inIsolation = this.commandLineOptions.InIsolation;
            if (inIsolation)
            {
                commandsUsed.Add("/InIsolation");
            }

            var useVsixExtensions = this.commandLineOptions.UseVsixExtensions;
            if (useVsixExtensions)
            {
                commandsUsed.Add("/UseVsixExtensions");
            }

            var frameworkVersionSpecified = this.commandLineOptions.FrameworkVersionSpecified;
            if (frameworkVersionSpecified)
            {
                commandsUsed.Add("/Framework");
            }

            var settings = this.commandLineOptions.SettingsFile;
            if (!string.IsNullOrEmpty(settings))
            {
                var extension = Path.GetExtension(settings);
                if (string.Equals(extension, ".runsettings", StringComparison.OrdinalIgnoreCase))
                {
                    commandsUsed.Add("/settings//.RunSettings");
                }
                else if (string.Equals(extension, ".testsettings", StringComparison.OrdinalIgnoreCase))
                {
                    commandsUsed.Add("/settings//.TestSettings");
                }
                else if (string.Equals(extension, ".vsmdi", StringComparison.OrdinalIgnoreCase))
                {
                    commandsUsed.Add("/settings//.vsmdi");
                }
                else if (string.Equals(extension, ".testrunConfig", StringComparison.OrdinalIgnoreCase))
                {
                    commandsUsed.Add("/settings//.testrunConfig");
                }
            }

            requestData.MetricsCollection.Add(TelemetryDataConstants.CommandLineSwitches, string.Join(",", commandsUsed.ToArray()));
        }

        /// <summary>
        /// Gets Request Data
        /// </summary>
        /// <param name="protocolConfig">Protocol Config</param>
        /// <returns></returns>
        private IRequestData GetRequestData(ProtocolConfig protocolConfig)
        {
            return new RequestData
            {
                ProtocolConfig = protocolConfig,
                MetricsCollection =
                               this.telemetryOptedIn || IsTelemetryOptedIn()
                                   ? (IMetricsCollection)new MetricsCollection()
                                   : new NoOpMetricsCollection(),
                IsTelemetryOptedIn = this.telemetryOptedIn || IsTelemetryOptedIn()
            };
        }

        private List<String> GetSources(TestRunRequestPayload testRunRequestPayload)
        {
            List<string> sources = new List<string>();
            if (testRunRequestPayload.Sources != null && testRunRequestPayload.Sources.Count > 0)
            {
                sources = testRunRequestPayload.Sources;
            }
            else if (testRunRequestPayload.TestCases != null && testRunRequestPayload.TestCases.Count > 0)
            {
                ISet<string> sourcesSet = new HashSet<string>();
                foreach (var testCase in testRunRequestPayload.TestCases)
                {
                    sourcesSet.Add(testCase.Source);
                }
                sources = sourcesSet.ToList();
            }
            return sources;
        }
    }
}
