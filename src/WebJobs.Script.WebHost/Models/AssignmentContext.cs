// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Models
{
    public class AssignmentContext
    {
        [JsonProperty("siteId")]
        public int SiteId { get; set; }

        [JsonProperty("siteName")]
        public string SiteName { get; set; }

        [JsonProperty("environment")]
        public Dictionary<string, string> Environment { get; set; }

        [JsonProperty("lastModifiedTime")]
        public DateTime LastModifiedTime { get; set; }
    }

    public static class AssignementContextExtensions
    {
        public static string GetZipUrl(this AssignmentContext context)
        {
            // TODO: move to const file
            const string setting = "WEBSITE_USE_ZIP";
            return context.Environment.ContainsKey(setting)
                ? context.Environment[setting]
                : string.Empty;
        }
    }
}
