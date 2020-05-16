using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Net;

namespace Ags.ResourceProxy.Core {
	public interface IProxyConfigService {

		ProxyConfig Config { get; }

		string ConfigPath { get; }

		bool IsAllowedReferrer(string referer);

		List<KeyValuePair<string, string>> GetOAuth2FormData(ServerUrl su, string proxyReferrer);

		List<KeyValuePair<string, string>> GetPortalExchangeTokenFormData(ServerUrl su, string proxyReferrer, string portalCode);

		List<KeyValuePair<string, string>> GetArcGISTokenFormData(ServerUrl su, string proxyReferrer);

		NetworkCredential GetCredentials(ServerUrl serverUrlConfig);

		ServerUrl GetProxyServerUrlConfig(string queryStringUrl);

		bool IsLoggingEnabled();

		ILogger GetLogger();
	}
}