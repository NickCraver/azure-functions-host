// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Management.Models;
using Microsoft.Azure.WebJobs.Script.WebHost.Management;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Extensions
{
    public static class FunctionMetadataExtensions
    {
        /// <summary>
        /// Maps FunctionMetadata to FunctionMetadataResponse.
        /// </summary>
        /// <param name="functionMetadata">FunctionMetadata to be mapped.</param>
        /// <param name="hostOptions">The host options</param>
        /// <returns>Promise of a FunctionMetadataResponse</returns>
        public static async Task<FunctionMetadataResponse> ToFunctionMetadataResponse(this FunctionMetadata functionMetadata, ScriptJobHostOptions hostOptions, string routePrefix, string baseUrl)
        {
            string functionPath = GetFunctionPathOrNull(hostOptions.RootScriptPath, functionMetadata.Name);
            string functionMetadataFilePath = GetMetadataPathOrNull(functionPath);

            if (string.IsNullOrEmpty(baseUrl))
            {
                baseUrl = "https://localhost/";
            }

            var response = new FunctionMetadataResponse
            {
                Name = functionMetadata.Name,
                Href = GetFunctionHref(functionMetadata.Name, baseUrl),
                Config = await GetFunctionConfig(functionMetadata, functionMetadataFilePath),

                // Properties below this comment are not present in the kudu version.
                IsDirect = functionMetadata.IsDirect(),
                IsDisabled = functionMetadata.IsDisabled(),
                IsProxy = false,
                Language = functionMetadata.Language,
                InvokeUrlTemplate = GetFunctionInvokeUrlTemplate(baseUrl, functionMetadata, routePrefix)
            };

            if (!string.IsNullOrEmpty(functionPath))
            {
                response.ScriptRootPathHref = VirtualFileSystem.FilePathToVfsUri(functionPath, baseUrl, hostOptions, isDirectory: true);
            }

            if (!string.IsNullOrEmpty(functionMetadataFilePath))
            {
                response.ConfigHref = VirtualFileSystem.FilePathToVfsUri(functionMetadataFilePath, baseUrl, hostOptions);
            }

            if (!string.IsNullOrEmpty(hostOptions.TestDataPath))
            {
                var testDataFilePath = functionMetadata.GetTestDataFilePath(hostOptions);
                response.TestDataHref = VirtualFileSystem.FilePathToVfsUri(testDataFilePath, baseUrl, hostOptions);
                response.TestData = await GetTestData(testDataFilePath, hostOptions);
            }

            if (!string.IsNullOrEmpty(functionMetadata.ScriptFile))
            {
                response.ScriptHref = VirtualFileSystem.FilePathToVfsUri(Path.Combine(hostOptions.RootScriptPath, functionMetadata.ScriptFile), baseUrl, hostOptions);
            }

            return response;
        }

        /// <summary>
        /// This method converts a FunctionMetadata into a JObject
        /// the scale controller understands. It's mainly the trigger binding
        /// with functionName inserted in it.
        /// </summary>
        /// <param name="functionMetadata">FunctionMetadata object to convert to a JObject.</param>
        /// <param name="config">ScriptHostConfiguration to read RootScriptPath from.</param>
        /// <returns>JObject that represent the trigger for scale controller to consume</returns>
        public static async Task<JObject> ToFunctionTrigger(this FunctionMetadata functionMetadata, ScriptJobHostOptions config)
        {
            var functionPath = Path.Combine(config.RootScriptPath, functionMetadata.Name);
            var functionMetadataFilePath = Path.Combine(functionPath, ScriptConstants.FunctionMetadataFileName);

            // Read function.json as a JObject
            var functionConfig = await GetFunctionConfig(functionMetadata, functionMetadataFilePath);

            if (functionConfig.TryGetValue("bindings", out JToken value) &&
                value is JArray)
            {
                // Find the trigger and add functionName to it
                foreach (JObject binding in (JArray)value)
                {
                    var type = (string)binding["type"];
                    if (type != null && type.EndsWith("Trigger", StringComparison.OrdinalIgnoreCase))
                    {
                        binding.Add("functionName", functionMetadata.Name);
                        return binding;
                    }
                }
            }

            // If the function has no trigger return null
            return null;
        }

        public static string GetTestDataFilePath(this FunctionMetadata functionMetadata, ScriptJobHostOptions hostOptions) =>
            GetTestDataFilePath(functionMetadata.Name, hostOptions);

        public static string GetTestDataFilePath(string functionName, ScriptJobHostOptions hostOptions) =>
            Path.Combine(hostOptions.TestDataPath, $"{functionName}.dat");

        private static string GetFunctionPathOrNull(string scriptRoot, string functionName)
        {
            var functionPath = Path.Combine(scriptRoot, functionName);

            if (FileUtility.DirectoryExists(functionPath))
            {
                return functionPath;
            }

            return null;
        }

        private static string GetMetadataPathOrNull(string functionPath)
        {
            if (!string.IsNullOrEmpty(functionPath))
            {
                var metadataPath = Path.Combine(functionPath, ScriptConstants.FunctionMetadataFileName);

                if (FileUtility.FileExists(metadataPath))
                {
                    return metadataPath;
                }
            }

            return null;
        }

        private static async Task<JObject> GetFunctionConfig(FunctionMetadata metadata, string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && FileUtility.FileExists(path))
                {
                    return await GetFunctionConfigFromFile(path);
                }
                else
                {
                    return GetFunctionConfigFromMetadata(metadata);
                }
            }
            catch
            {
                // no-op
            }

            // If there are any errors parsing function.json return an empty object.
            // This is current kudu behavior.
            // TODO: add an error field that can be used to communicate the JSON parse error.
            return new JObject();
        }

        private static async Task<JObject> GetFunctionConfigFromFile(string path)
        {
            return JObject.Parse(await FileUtility.ReadAsync(path));
        }

        private static JObject GetFunctionConfigFromMetadata(FunctionMetadata metadata)
        {
            var config = new
            {
                name = metadata.Name,
                entryPoint = metadata.EntryPoint,
                scriptFile = metadata.ScriptFile,
                language = metadata.Language,
                functionDirectory = metadata.FunctionDirectory,
                bindings = metadata.Bindings.Select(m => m.Raw).ToList()
            };

            return JObject.FromObject(config);
        }

        private static async Task<string> GetTestData(string testDataPath, ScriptJobHostOptions config)
        {
            if (!File.Exists(testDataPath))
            {
                FileUtility.EnsureDirectoryExists(Path.GetDirectoryName(testDataPath));
                await FileUtility.WriteAsync(testDataPath, string.Empty);
            }

            string data = await FileUtility.ReadAsync(testDataPath);

            // We avoid inlining test data beyond the specified length. We do this to
            // provide back compat for the majority of cases. In all other cases, the caller
            // is expected to follow TestDataHref to retrieve the data.
            if (data.Length > ScriptConstants.MaxTestDataInlineStringLength &&
                SystemEnvironment.Instance.GetEnvironmentVariableOrDefault(EnvironmentSettingNames.TestDataCapEnabled, "1") == "1")
            {
                return null;
            }

            return data;
        }

        private static Uri GetFunctionHref(string functionName, string baseUrl) =>
            new Uri($"{baseUrl}/admin/functions/{functionName}");

        internal static Uri GetFunctionInvokeUrlTemplate(string baseUrl, FunctionMetadata functionMetadata, string routePrefix)
        {
            var httpBinding = functionMetadata.InputBindings.FirstOrDefault(p => string.Compare(p.Type, "httpTrigger", StringComparison.OrdinalIgnoreCase) == 0);

            if (httpBinding != null)
            {
                string customRoute = null;
                if (httpBinding.Raw != null && httpBinding.Raw.TryGetValue("route", StringComparison.OrdinalIgnoreCase, out JToken value))
                {
                    // a custom route is specified
                    customRoute = (string)value;
                }

                string uriString = baseUrl.TrimEnd('/');
                if (!string.IsNullOrEmpty(routePrefix))
                {
                    uriString += $"/{routePrefix.TrimEnd('/')}";
                }

                if (!string.IsNullOrEmpty(customRoute))
                {
                    uriString += $"/{customRoute}";
                }
                else
                {
                    uriString += $"/{functionMetadata.Name}";
                }

                return new Uri(uriString.ToLowerInvariant());
            }

            return null;
        }
    }
}