using Ags.ResourceProxy.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using System.Net.Http;

namespace Ags.ResourceProxy.Web
{
	public class Startup {
		public Startup(IConfiguration configuration) {
			Configuration = configuration;
		}

		public IConfiguration Configuration { get; }

		// This method gets called by the runtime. Use this method to add services to the container.
		public void ConfigureServices(IServiceCollection services) {

			services.AddMemoryCache();

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

			// If using IIS:
			services.Configure<IISServerOptions>(options =>
			{
				options.AllowSynchronousIO = true;
			});

			// services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_1);

		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IWebHostEnvironment env) {
			if (env.IsDevelopment()) {
				app.UseDeveloperExceptionPage();
			} else {
				app.UseExceptionHandler("/Error");
				app.UseHsts();
			}

			app.UseHttpsRedirection();
			app.UseStaticFiles();
			app.UseCookiePolicy();

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

			// app.UseMvc();
			app.UseRouting();
		}
	}
}
