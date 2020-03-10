// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.Utilities
{
    using System.Diagnostics.CodeAnalysis;
    using System.Xml.XPath;

    /// <summary>
    /// Utility class for MaxCpuCount element of RunSetting
    /// </summary>
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "Reviewed. Suppression is OK here.")]
    public static class ParallelRunSettingsUtilities
    {
        private static string XpathOfRunSettings = @"/RunSettings";
        private static string XpathOfRunConfiguration = @"/RunSettings/RunConfiguration";
        private static string XpathOfMaxCpuCount = @"/RunSettings/RunConfiguration/MaxCpuCount";

        /// <summary>
        /// The MaxCpuCount setting template.
        /// </summary>
        private const string MaxCpuCountSettingTemplate = @"<MaxCpuCount>0</MaxCpuCount>";

        /// <summary>
        /// The RunConfiguration with MaxCpuCount setting template.
        /// </summary>
        private const string RunConfigurationWithMaxCpuCountSettingTemplate = @"<RunConfiguration>
                                                                                            <MaxCpuCount>0</MaxCpuCount>
                                                                                          </RunConfiguration>";

        /// <summary>
        /// This will update the RunSetting with MaxCpuCount 0 if RunSetting doesn't configured with this setting.
        /// </summary>
        /// <param name="runSettingsDocument">RunSetting file.</param>
        public static void UpdateRunSettingsWithParallelSettingIfNotConfigured(XPathNavigator navigator)
        {
            var node = navigator.SelectSingleNode(XpathOfMaxCpuCount);
            // run settings given by user takes precedence over parallel switch
            if (node == null)
            {
                var runConfigurationNavigator = navigator.SelectSingleNode(XpathOfRunConfiguration);
                if (runConfigurationNavigator != null)
                {
                    runConfigurationNavigator.AppendChild(MaxCpuCountSettingTemplate);
                }
                else
                {
                    runConfigurationNavigator = navigator.SelectSingleNode(XpathOfRunSettings);
                    runConfigurationNavigator?.AppendChild(RunConfigurationWithMaxCpuCountSettingTemplate);
                }
            }
        }
    }
}
