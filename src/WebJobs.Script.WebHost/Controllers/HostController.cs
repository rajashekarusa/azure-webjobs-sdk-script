// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.WebApiCompatShim;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Script;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.WebHost.Authentication;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;
using Microsoft.Azure.WebJobs.Script.WebHost.Security.Authorization.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Controllers
{
    /// <summary>
    /// Controller responsible for handling all administrative requests, for
    /// example enqueueing function invocations, etc.
    /// </summary>
    public class HostController : Controller
    {
        private static string _assignedApp = null;
        private static readonly object _assignLock = new object();

        private readonly WebScriptHostManager _scriptHostManager;
        private readonly WebHostSettings _webHostSettings;
        private readonly ILogger _logger;
        private readonly IAuthorizationService _authorizationService;

        public HostController(WebScriptHostManager scriptHostManager, WebHostSettings webHostSettings, ILoggerFactory loggerFactory, IAuthorizationService authorizationService)
        {
            _scriptHostManager = scriptHostManager;
            _webHostSettings = webHostSettings;
            _logger = loggerFactory?.CreateLogger(ScriptConstants.LogCategoryHostController);
            _authorizationService = authorizationService;
        }

        [HttpGet("admin/host/status")]
        //[Authorize(Policy = PolicyNames.AdminAuthLevelOrInternal)]
        public IActionResult GetHostStatus()
        {
            var status = new HostStatus
            {
                State = _scriptHostManager.State.ToString(),
                Version = ScriptHost.Version,
                VersionDetails = Utility.GetInformationalVersion(typeof(ScriptHost)),
                Id = _scriptHostManager.Instance?.ScriptConfig.HostConfig.HostId
            };

            var lastError = _scriptHostManager.LastError;
            if (lastError != null)
            {
                status.Errors = new Collection<string>();
                status.Errors.Add(Utility.FlattenException(lastError));
            }

            var parameters = Request.Query;
            if (parameters.TryGetValue(ScriptConstants.CheckLoadQueryParameterName, out StringValues value) && value == "1")
            {
                status.Load = new LoadStatus
                {
                    IsHigh = _scriptHostManager.PerformanceManager.IsUnderHighLoad()
                };
            }

            string message = $"Host Status: {JsonConvert.SerializeObject(status, Formatting.Indented)}";
            _logger?.LogInformation(message);

            return Ok(status);
        }

        [HttpPost("admin/host/ping")]
        public IActionResult Ping()
        {
            return Ok();
        }

        [HttpPost("admin/host/log")]
        //[Authorize(Policy = PolicyNames.AdminAuthLevelOrInternal)]
        public IActionResult Log(IEnumerable<HostLogEntry> logEntries)
        {
            if (logEntries == null)
            {
                return BadRequest("An array of log entry objects is expected.");
            }
            foreach (var logEntry in logEntries)
            {
                var traceEvent = new TraceEvent(logEntry.Level, logEntry.Message, logEntry.Source);
                if (!string.IsNullOrEmpty(logEntry.FunctionName))
                {
                    traceEvent.Properties.Add(ScriptConstants.TracePropertyFunctionNameKey, logEntry.FunctionName);
                }

                var logLevel = Utility.ToLogLevel(traceEvent.Level);
                var logData = new Dictionary<string, object>
                {
                    ["Source"] = logEntry.Source,
                    ["FunctionName"] = logEntry.FunctionName
                };
                _logger.Log(logLevel, 0, logData, null, (s, e) => logEntry.Message);
            }

            return Ok();
        }

        [HttpPost("admin/host/debug")]
        //[Authorize(Policy = PolicyNames.AdminAuthLevel)]
        public IActionResult LaunchDebugger()
        {
            if (_webHostSettings.IsSelfHost)
            {
                // If debugger is already running, this will be a no-op returning true.
                if (Debugger.Launch())
                {
                    return Ok();
                }
                else
                {
                    return StatusCode(StatusCodes.Status409Conflict);
                }
            }

            return StatusCode(StatusCodes.Status501NotImplemented);
        }

        public IActionResult AlreadyAssigned() =>
            StatusCode(StatusCodes.Status409Conflict, "Instance already assigned");

        [HttpPost("admin/assign")]
        [HttpPost("admin/instance/assign")]
        public IActionResult Assign([FromBody] AssignmentContext assignmentContext)
        {
            if (string.IsNullOrEmpty(_assignedApp))
            {
                try
                {
                    lock (_assignLock)
                    {
                        if (!string.IsNullOrEmpty(_assignedApp))
                        {
                            return AlreadyAssigned();
                        }

                        // decrypt, //TODO
                        //var assignmentContext = JsonConvert.DeserializeObject<AssignmentContext>(body);
                        _assignedApp = assignmentContext.SiteName;
                    }

                    // download zip
                    var zip = assignmentContext.GetZipUrl();
                    void applyAppSettings()
                    {
                        // apply app settings
                        foreach (var pair in assignmentContext.Environment)
                        {
                            System.Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                            ScriptSettingsManager.Instance.SetSetting(pair.Key, pair.Value);
                        }
                    }

                    if (!string.IsNullOrEmpty(zip))
                    {
                        var filePath = Path.GetTempFileName();
                        using (var webClient = new WebClient())
                        {
                            webClient.DownloadFile(new Uri(zip), filePath);
                        }

                        applyAppSettings();

                        ZipFile.ExtractToDirectory(filePath, _webHostSettings.ScriptPath, overwriteFiles: true);

                    }
                    else
                    {
                        applyAppSettings();
                    }

                    // Restart runtime.
                    _scriptHostManager.RestartHost();
                    return Accepted();
                }
                catch (Exception)
                {
                    throw;
                }
                finally
                {
                }
            }
            else
            {
                // Decrypt.
                return AlreadyAssigned();
            }
        }

        [HttpGet("admin/info")]
        [HttpGet("admin/instance/info")]
        public IActionResult GetInstanceInfo()
        {
            return Ok(new Dictionary<string, string>
            {
                { "FUNCTIONS_EXTENSION_VERSION", ScriptHost.Version },
                { "WEBSITE_NODE_DEFAULT_VERSION", "8.5.0" }
            });
        }

        [HttpGet("admin/instance/status")]
        [HttpGet("admin/status")]
        public IActionResult GetInstanceStatus()
        {
            return Ok();
        }

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            // For all admin api requests, we'll update the ScriptHost debug timeout
            // For now, we'll enable debug mode on ANY admin requests. Since the Portal interacts through
            // the admin API this is sufficient for identifying when the Portal is connected.
            _scriptHostManager.Instance?.NotifyDebug();

            base.OnActionExecuting(context);
        }

        [HttpGet]
        [HttpPost]
        //[Authorize(AuthenticationSchemes = AuthLevelAuthenticationDefaults.AuthenticationScheme)]
        [Route("runtime/webhooks/{name}/{*extra}")]
        public async Task<IActionResult> ExtensionWebHookHandler(string name, CancellationToken token)
        {
            var provider = _scriptHostManager.BindingWebHookProvider;

            var handler = provider.GetHandlerOrNull(name);
            if (handler != null)
            {
                string keyName = WebJobsSdkExtensionHookProvider.GetKeyName(name);
                var authResult = await _authorizationService.AuthorizeAsync(User, keyName, PolicyNames.SystemAuthLevel);
                if (!authResult.Succeeded)
                {
                    return Unauthorized();
                }

                var requestMessage = new HttpRequestMessageFeature(this.HttpContext).HttpRequestMessage;
                HttpResponseMessage response = await handler.ConvertAsync(requestMessage, token);

                var result = new ObjectResult(response);
                result.Formatters.Add(new HttpResponseMessageOutputFormatter());
                return result;
            }

            return NotFound();
        }
    }
}
