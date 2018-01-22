using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Azure.WebJobs.Script.WebHost.Features;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Middleware
{
    internal class ProxyMiddleware
    {
        private readonly RequestDelegate _next;

        public ProxyMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context)
        {
            await _next(context);

            IFunctionExecutionFeature functionExecution = context.Features.Get<IFunctionExecutionFeature>();

            // HttpBufferingService is disabled for non-proxy functions.
            if (functionExecution != null && !functionExecution.Descriptor.Metadata.IsProxy)
            {
                var bufferingFeature = context.Features.Get<IHttpBufferingFeature>();
                bufferingFeature?.DisableRequestBuffering();
                bufferingFeature?.DisableResponseBuffering();
            }
        }
    }
}
