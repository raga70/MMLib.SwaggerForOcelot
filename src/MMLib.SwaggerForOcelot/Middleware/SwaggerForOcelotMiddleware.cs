﻿using Kros.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMLib.SwaggerForOcelot.Configuration;
using MMLib.SwaggerForOcelot.Transformation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MMLib.SwaggerForOcelot.Middleware
{
    /// <summary>
    /// Swagger for Ocelot middleware.
    /// This middleware generate swagger documentation from downstream services for SwaggerUI.
    /// </summary>
    public class SwaggerForOcelotMiddleware
    {
        private readonly RequestDelegate _next;

        private readonly IOptions<List<ReRouteOptions>> _reRoutes;
        private readonly Lazy<Dictionary<string, SwaggerEndPointOptions>> _swaggerEndPoints;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ISwaggerJsonTransformer _transformer;
        private readonly SwaggerForOcelotUIOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="SwaggerForOcelotMiddleware"/> class.
        /// </summary>
        /// <param name="next">The next delegate.</param>
        /// <param name="options">The options.</param>
        /// <param name="reRoutes">The Ocelot ReRoutes configuration.</param>
        /// <param name="swaggerEndPoints">The swagger end points.</param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="transformer">The SwaggerJsonTransformer</param>
        public SwaggerForOcelotMiddleware(
            RequestDelegate next,
            SwaggerForOcelotUIOptions options,
            IOptions<List<ReRouteOptions>> reRoutes,
            IOptions<List<SwaggerEndPointOptions>> swaggerEndPoints,
            IHttpClientFactory httpClientFactory,
            ISwaggerJsonTransformer transformer)
        {
            _transformer = Check.NotNull(transformer, nameof(transformer));
            _next = Check.NotNull(next, nameof(next));
            _reRoutes = Check.NotNull(reRoutes, nameof(reRoutes));
            Check.NotNull(swaggerEndPoints, nameof(swaggerEndPoints));
            _httpClientFactory = Check.NotNull(httpClientFactory, nameof(httpClientFactory));
            _options = options;

            _swaggerEndPoints = new Lazy<Dictionary<string, SwaggerEndPointOptions>>(()
                => swaggerEndPoints.Value.ToDictionary(p => $"/{p.KeyToPath}", p => p));
        }

        /// <summary>
        /// Invokes the specified context.
        /// </summary>
        /// <param name="context">The context.</param>
        public async Task Invoke(HttpContext context)
        {
            var endPoint = GetEndPoint(context.Request.Path);
            var httpClient = _httpClientFactory.CreateClient();
            AddHeaders(httpClient);
            var content = await httpClient.GetStringAsync(endPoint.Url);
            var hostName = endPoint.EndPoint.HostOverride ?? context.Request.Host.Value;
            var reRouteOptions = ExpandReRouteOptions(endPoint.EndPoint);

            content = _transformer.Transform(content, reRouteOptions, hostName);
            content = await ReconfigureUpstreamSwagger(context, content);

            await context.Response.WriteAsync(content);
        }

        private IEnumerable<ReRouteOptions> ExpandReRouteOptions(SwaggerEndPointOptions endPoint)
        {
            var reRouteOptions = _reRoutes.Value.Where(p => p.SwaggerKey == endPoint.Key).ToList();

            if (string.IsNullOrWhiteSpace(endPoint.VersionPlaceholder)) 
                return reRouteOptions;

            var versionReRouteOptions = reRouteOptions.Where(x =>
                x.DownstreamPathTemplate.Contains(endPoint.VersionPlaceholder)
                || x.UpstreamPathTemplate.Contains(endPoint.VersionPlaceholder)).ToList();
            versionReRouteOptions.ForEach(o => reRouteOptions.Remove(o));
            foreach(var reRouteOption in versionReRouteOptions) {
                var versionMappedReRouteOptions = endPoint.Config.Select(c => new ReRouteOptions()
                {
                    SwaggerKey = reRouteOption.SwaggerKey,
                    DownstreamPathTemplate =
                        reRouteOption.DownstreamPathTemplate.Replace(endPoint.VersionPlaceholder,
                            c.Version),
                    UpstreamHttpMethod = reRouteOption.UpstreamHttpMethod,
                    UpstreamPathTemplate =
                        reRouteOption.UpstreamPathTemplate.Replace(endPoint.VersionPlaceholder,
                            c.Version),
                    VirtualDirectory = reRouteOption.VirtualDirectory
                });
                reRouteOptions.AddRange(versionMappedReRouteOptions);
            }

            return reRouteOptions;
        }

        private async Task<string> ReconfigureUpstreamSwagger(HttpContext context, string swaggerJson)
        {
            if (_options.ReConfigureUpstreamSwaggerJson != null && _options.ReConfigureUpstreamSwaggerJsonAsync != null)
            {
                throw new Exception(
                    "Both ReConfigureUpstreamSwaggerJson and ReConfigureUpstreamSwaggerJsonAsync cannot have a value. Only use one method.");
            }

            if (_options.ReConfigureUpstreamSwaggerJson != null)
            {
                return _options.ReConfigureUpstreamSwaggerJson(context, swaggerJson);
            }

            if (_options.ReConfigureUpstreamSwaggerJsonAsync != null)
            {
                return await _options.ReConfigureUpstreamSwaggerJsonAsync(context, swaggerJson);
            }

            return swaggerJson;
        }

        private void AddHeaders(HttpClient httpClient)
        {
            if (_options.DownstreamSwaggerHeaders == null) return;
            foreach (var kvp in _options.DownstreamSwaggerHeaders)
            {
                httpClient.DefaultRequestHeaders.Add(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// Get Url and Endpoint from path
        /// </summary>
        /// <param name="path"></param>
        /// <returns>
        /// The Url of a specific version and <see cref="SwaggerEndPointOptions"/>.
        /// </returns>
        private (string Url, SwaggerEndPointOptions EndPoint) GetEndPoint(string path)
        {
            var endPointInfo = GetEndPointInfo(path);
            var endPoint = _swaggerEndPoints.Value[$"/{endPointInfo.Key}"];
            var url = endPoint.Config.FirstOrDefault(x => x.Version == endPointInfo.Version)?.Url;
            return (url, endPoint);
        }

        /// <summary>
        /// Get url and version from Path
        /// </summary>
        /// <param name="path"></param>
        /// <returns>
        /// Version and the key of End point
        /// </returns>
        private (string Version, string Key) GetEndPointInfo(string path)
        {
            var keys = path.Split('/');
            return (keys[1], keys[2]);
        }
    }
}
