# AGS (ArcGIS Server) .Net Core Resource-Proxy

ArcGIS Server resource proxy for .Net Core. This proxy is like the <https://github.com/Esri/resource-proxy> but has been updated to work with .Net Core.
The latest version supports changes to the OAuth2 endpoint introduced in v10.0 of the ArcGIS REST API.

This repo is a fork from: <https://github.com/dgwaldo/ags-resource-proxy>. He deserves all the credit for this. I have only added support for non network based authentication using username, password to a specific tokenUrl.

This fork also updates the library to dotnet core 3.1.

## Features

- Accessing cross domain resources
- Requests that exceed 2048 characters
- Accessing resources secured with Microsoft Integrated Windows Authentication (IWA)
  - using application pool identity for the hosted resource-proxy.
    - using proxied user credentials
- OAuth 2.0 app logins.
- Memory based cache of tokens. (If your environment is load balanced, this may be an issue).

## Not supported

- This proxy does not do rate limiting.
- This proxy does not let you set an access token in configuration, though the OAuth2 flow in the proxy will get acquire an access token.
- This proxy does not do any logging.

## Instructions

Install the package off Nuget. PM> Install-Package Ags.ResourceProxy.Core -Version 1.0.0

Place the proxy config file into the root of your application directory, (location is configurable).

Example wireup can be seen in the web project.

```json
// Proxy Configuration (proxy.config.json)
{
  // Allowed referrers must contain an exact URL match use "*" to match any referrer.
  "allowedReferrers": [ "*" ],
  // Set use app pool identity to use the same network credentials as the app process running in IIS
  "useAppPoolIdentity": false,
  // Token cache time given in minutes. Should be = or < timeout returned in tokens.
  "tokenCacheMinutes": 29,
  // Array of root URLS to be proxied
  "serverUrls": [
    // Example using IWA to authenticate with the server
    {
      "url": "https://arcgisserver.yourdomain.com/webapdater/",
      "domain": "yourdomain",
      "username": "username",
      "password": "password"
    },
    // Example using username/password to get token to authenticate with the server
    {
      "url": "https://arcgisserver.yourdomain.com/webapdater/",
      "username": "username",
      "password": "password",
      "tokenUrl": "https://arcgisserver.yourdomain.com/arcgis/sharing/rest/generateToken",
      "referer": "my-other-domain"
    },
    // Example using using client and client secret to get OAuth tokens.
    // Note: IWA credentials can also be passed for environments where IT has the token endpoint behind IWA.
    {
      "url": "arcgis.com",
      "clientId": "clientid",
      "clientSecret": "clientsecret",
      "oauth2Endpoint": "https://www.arcgis.com/sharing/rest/oauth2/token"
    }
  ]}
```

In your .Net Core ASP project locate the Program.cs file. Add the following static configuration.

You can change the file name here if you want to use a different location.

```C#
public static IConfiguration ProxyConfiguration { get; } = new ConfigurationBuilder()
  .SetBasePath(Directory.GetCurrentDirectory())
  .AddJsonFile("proxy.config.json")
  .Build();
```

In your .Net Core ASP project locate the startup.cs file. In the ConfigureServices method add the following code.z

```C#
// This method gets called by the runtime. Use this method to add services to the container.
public void ConfigureServices(IServiceCollection services) {
  // Your code above here
  var proxyConfig = Program.ProxyConfiguration.Get<ProxyConfig>();
  var proxyConfigService = new ProxyConfigService(proxyConfig);
  services.AddSingleton<IProxyConfigService, ProxyConfigService>((a) => proxyConfigService);
  services.AddSingleton<IProxyService, ProxyService>();

  proxyConfig.ServerUrls.ToList().ForEach(su => {
    services.AddHttpClient(su.Url)
      .ConfigurePrimaryHttpMessageHandler(h => {
        return new HttpClientHandler {
          AllowAutoRedirect = false,
          Credentials = proxyConfigService.GetCredentials(proxyConfigService.GetProxyServerUrlConfig((su.Url)))
        };
      });
  });
  // Your code below herez
}
```

Next add the following to the Configure method.

```C#
public void Configure(IApplicationBuilder app, IWebHostEnvironment env) {
  // Your code above
  app.UseWhen(context => {
        return context.Request.Path.Value.ToLower().StartsWith(@"/proxy/proxy.ashx", StringComparison.OrdinalIgnoreCase);
        //&& context.User.Identity.IsAuthenticated; // Add this back in to keep unauthenticated users from utilzing the proxy.
      },
        builder =>
          builder.UseAgsProxyServer(
          app.ApplicationServices.GetService<IProxyConfigService>(),
          app.ApplicationServices.GetService<IProxyService>(),
          app.ApplicationServices.GetService<IMemoryCache>())
        );
  // Your code below
  // eg. app.UseMvc();
}
```

Note: You can control access to the proxy by removing the comment on the check for authenticated users.
Also, you can control route used by the proxy by modifying the path within the StartsWith() method. The example above sets it to server from the same location as the old ashx proxy from ESRI.

### Contributions

Feel free to file an issue or open a pull request to extend the functionality of this code.

### License

MIT
