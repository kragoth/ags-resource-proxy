using Microsoft.AspNetCore.Http;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Ags.ResourceProxy.Core {

	public class ProxyServerMiddleware {
		private readonly DateTime EpochDate = new DateTime(1970, 1, 1);

		private const string ArcGisJsonTokenKey = "token";
		private const string ArcGisJsonExpirationKey = "expires";
		private const string JsonExpirationKey = "expires_in";
        private const string JsonTokenKey = "access_token";

		private readonly IMemoryCache _cache;
		private readonly IProxyConfigService _proxyConfigService;
		private readonly IProxyService _proxyService;
		private readonly RequestDelegate _next;
		private string _proxyReferrer;

		public ProxyServerMiddleware(
			RequestDelegate next,
			IProxyConfigService proxyConfigService,
			IProxyService proxyService,
			IMemoryCache memoryCache
			) {
			_next = next;
			_proxyConfigService = proxyConfigService ?? throw new ArgumentNullException(nameof(proxyConfigService));
			_proxyService = proxyService ?? throw new ArgumentNullException(nameof(proxyService));
			_cache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
		}

		private void Log(LogLevel level, string message, Exception e = null, object[] args = null)
		{
			if (_proxyConfigService.IsLoggingEnabled())
			{
				if (e != null)
				{
					_proxyConfigService.GetLogger().Log(level, e, message, args);
				} else
				{
					_proxyConfigService.GetLogger().Log(level, message, args);
				}
			}
		}

		public async Task Invoke(HttpContext context) {
			try
			{
				var endRequest = false;

				if (context.Request.QueryString.HasValue && context.Request.QueryString.ToString().ToLower() == "?ping") {
					await context.Response.WriteAsync(CreatePingResponse());
					return;
				}

				// Note: Referrer is mis-spelled in the HTTP Spec
				_proxyReferrer = context?.Request?.Headers["referer"];
				if (_proxyConfigService.IsAllowedReferrer(_proxyReferrer) == false) {
					CreateErrorResponse(context.Response, $"Referrer {_proxyReferrer} is not allowed.", HttpStatusCode.BadRequest);
					return;
				}
				// Allows request body to be read multiple times, and buffers.
				context.Request.EnableBuffering();

				var proxiedUrl = context.Request.QueryString.ToString().TrimStart('?');

				// Check if proxy URL is in the list of configured URLs.
				var serverUrlConfig = _proxyConfigService.GetProxyServerUrlConfig(proxiedUrl);

				HttpResponseMessage response = null;

				if (serverUrlConfig != null) {
					var isTokenLogin = !String.IsNullOrEmpty(serverUrlConfig?.Username) && !String.IsNullOrEmpty(serverUrlConfig?.TokenUrl);
					var isAppLogin = !String.IsNullOrEmpty(serverUrlConfig?.ClientId) && !String.IsNullOrEmpty(serverUrlConfig?.ClientSecret);
					var isUserLogin = !String.IsNullOrEmpty(serverUrlConfig?.Username) && !String.IsNullOrEmpty(serverUrlConfig?.Password);
					var httpClientName = serverUrlConfig?.Url;

					if (isAppLogin || isTokenLogin) {
						Log(LogLevel.Debug, "ArcGIS Proxy: Arcgis Token required");
						var serverToken = await CacheTryGetServerToken(serverUrlConfig, httpClientName);
						response = await _proxyService.ForwardRequestToServer(context.Request, proxiedUrl, httpClientName, serverToken);
					} else if (isUserLogin) {
						response = await _proxyService.ForwardRequestToServer(context.Request, proxiedUrl, httpClientName);
					}
				} else { // No matching url to proxy, bypass and proxy the request.
					response = await _proxyService.ForwardRequestToServer(context.Request, proxiedUrl, "");
				}

				await CopyProxyHttpResponse(context, response);

				endRequest = true;
				if (!endRequest) {
					await _next(context);
				}
			}
			catch (TaskCanceledException e) {
				// NOOP: Zooming/panning quickly result in MANY task cancelled exceptions which we do not care about
				Log(LogLevel.Debug, $"ArcGIS Proxy: Task cancelled", e);
			} catch(Newtonsoft.Json.JsonReaderException e) {
				Log(LogLevel.Debug, $"ArcGIS Proxy: Json Parse Error", e);
				throw;
			}

		}

		private async Task<string> CacheTryGetServerToken(ServerUrl su, string clientName, bool killCache = false) {

			var tokenCacheKey = $"token_for_{su.Url}";
			JObject o;
			Log(LogLevel.Debug, $"ArcGIS Proxy: Token cache key: {tokenCacheKey}");
			if (!_cache.TryGetValue(tokenCacheKey, out string serverTokenJson) || killCache) {
				Log(LogLevel.Debug, $"ArcGIS Proxy: No token in cache or token expired - requesting new token");
				var requestTime = DateTime.UtcNow;
				// Key not in cache, so get token.
				serverTokenJson = await GetAppToken(su, clientName);
				o = JObject.Parse(serverTokenJson);

				if (string.IsNullOrEmpty(su.TokenUrl))
				{
					// Set expiration based on value returned with access token
					_cache.Set(tokenCacheKey, serverTokenJson, TimeSpan.FromSeconds(Convert.ToDouble(o[JsonExpirationKey])));
				} else
				{
					
					var offset = TimeSpan.FromMilliseconds(Convert.ToDouble(o[ArcGisJsonExpirationKey]));
					var expiry = EpochDate + offset;
					var timespan = expiry - requestTime;
					// Set expiration based on value returned with access token
					Log(LogLevel.Debug, $"ArcGIS Proxy: Setting token expiry to timespan of: {timespan}");
					_cache.Set(tokenCacheKey, serverTokenJson, timespan);
				}
			} else
			{
				Log(LogLevel.Debug, $"ArcGIS Proxy: Using cached token: {serverTokenJson}");
				o = JObject.Parse(serverTokenJson);
			}

			var requestToken = string.IsNullOrEmpty(su.TokenUrl) ? (string)o[JsonTokenKey] : (string)o[ArcGisJsonTokenKey];
			
			Log(LogLevel.Debug, $"ArcGIS Proxy: Proxying request with token: {requestToken}");

			return requestToken;
		}

		private async Task<string> GetAppToken(ServerUrl su, string clientName) {

			if (string.IsNullOrEmpty(su.Oauth2Endpoint) && string.IsNullOrEmpty(su.TokenUrl)) {
				throw new ArgumentNullException("Oauth2Endpoint | TokenUrl");
			}

			if (string.IsNullOrEmpty(su.Oauth2Endpoint))
			{
				Log(LogLevel.Debug, $"ArcGIS Proxy: Using arcgis token generation endpoint: {su.TokenUrl}");
				var formData = _proxyConfigService.GetArcGISTokenFormData(su, _proxyReferrer ?? su.Referer);

				var tokenJson = await _proxyService.RequestTokenJson(su.TokenUrl, formData, clientName);
				Log(LogLevel.Debug, $"ArcGIS Proxy: Generate token response: {tokenJson}");
				return tokenJson;
			} else 
			{
				Log(LogLevel.Debug, $"ArcGIS Proxy: Using Oauth2 endpoint");
				var formData = _proxyConfigService.GetOAuth2FormData(su, _proxyReferrer);

				var tokenJson = await _proxyService.RequestTokenJson(su.Oauth2Endpoint, formData, clientName);
				return tokenJson;
			}
		}

		private async Task CopyProxyHttpResponse(HttpContext context, HttpResponseMessage responseMessage) {
			if (responseMessage == null) {
				throw new ArgumentNullException(nameof(responseMessage));
			}

			var response = context.Response;

			response.StatusCode = (int)responseMessage.StatusCode;
			foreach (var header in responseMessage?.Headers) {
				response.Headers[header.Key] = header.Value.ToArray();
			}

			foreach (var header in responseMessage?.Content?.Headers) {
				response.Headers[header.Key] = header.Value.ToArray();
			}

			// Removes the header so it doesn't expect a chunked response.
			response.Headers.Remove("transfer-encoding");

			// try
			// {
				using var responseStream = await responseMessage.Content.ReadAsStreamAsync();
				await responseStream.CopyToAsync(response.Body, context.RequestAborted);
			// }
			// catch (TaskCanceledException) {
			// 	// NOOP: Zooming/panning quickly result in MANY task cancelled exceptions which we do not care about
			// }
		}

		private HttpResponse CreateErrorResponse(HttpResponse httpResponse, string message, HttpStatusCode status) {
			var jsonMsg = JsonConvert.SerializeObject(new { message, status });
			httpResponse.StatusCode = (int)status;
			httpResponse.WriteAsync(jsonMsg);
			return httpResponse;
		}

		private string CreatePingResponse() {
			var pingResponse = new {
				message = "Pong!",
				hasConfig = _proxyConfigService.Config != null,
				referringUrl = _proxyReferrer
			};
			return JsonConvert.SerializeObject(pingResponse);
		}

	}
}
