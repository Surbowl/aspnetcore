// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Hosting
{
    /// <summary>
    /// A builder for <see cref="IWebHost"/>
    /// </summary>
    public class WebHostBuilder : IWebHostBuilder
    {
        private readonly HostingEnvironment _hostingEnvironment;
        private readonly IConfiguration _config;
        private readonly WebHostBuilderContext _context;

        /// <summary>
        /// Web 托管选项，执行 <see cref="Build"/> 的过程中在方法 <see cref="BuildCommonServices"/> 内实例化
        /// </summary>
        private WebHostOptions? _options;
        /// <summary>
        /// 是否执行过 <see cref="Build"/>
        /// </summary>
        private bool _webHostBuilt;
        /// <summary>
        /// 用于配置 host 或 web 应用附加服务的委托
        /// </summary>
        private Action<WebHostBuilderContext, IServiceCollection>? _configureServices;
        private Action<WebHostBuilderContext, IConfigurationBuilder>? _configureAppConfigurationBuilder;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebHostBuilder"/> class.
        /// </summary>
        public WebHostBuilder()
        {
            _hostingEnvironment = new HostingEnvironment();

            _config = new ConfigurationBuilder()
                .AddEnvironmentVariables(prefix: "ASPNETCORE_") // 添加带有 “ASPNETCORE_” 前缀的环境变量
                .Build();

            // 如果没有配置 ASPNETCORE_ENVIRONMENT，则使用 Hosting:Environment 或 ASPNET_ENV
            if (string.IsNullOrEmpty(GetSetting(WebHostDefaults.EnvironmentKey)))
            {
                // Try adding legacy environment keys, never remove these.
                UseSetting(WebHostDefaults.EnvironmentKey, Environment.GetEnvironmentVariable("Hosting:Environment")
                    ?? Environment.GetEnvironmentVariable("ASPNET_ENV"));
            }

            // 如果没有配置 ASPNETCORE_URLS，则使用 ASPNETCORE_SERVER.URLS
            if (string.IsNullOrEmpty(GetSetting(WebHostDefaults.ServerUrlsKey)))
            {
                // Try adding legacy url key, never remove this.
                UseSetting(WebHostDefaults.ServerUrlsKey, Environment.GetEnvironmentVariable("ASPNETCORE_SERVER.URLS"));
            }

            _context = new WebHostBuilderContext
            {
                Configuration = _config
            };
        }

        /// <summary>
        /// Get the setting value from the configuration.
        /// 从 IConfiguration 中获取 key 的对应值
        /// </summary>
        /// <param name="key">The key of the setting to look up.</param>
        /// <returns>The value the setting currently contains.</returns>
        public string GetSetting(string key)
        {
            return _config[key];
        }

        /// <summary>
        /// Add or replace a setting in the configuration.
        /// 向 IConfiguration 中添加键值对
        /// </summary>
        /// <param name="key">The key of the setting to add or replace.</param>
        /// <param name="value">The value of the setting to add or replace.</param>
        /// <returns>The <see cref="IWebHostBuilder"/>.</returns>
        public IWebHostBuilder UseSetting(string key, string? value)
        {
            _config[key] = value;
            return this;
        }

        /// <summary>
        /// Adds a delegate for configuring additional services for the host or web application. This may be called
        /// multiple times.
        /// 添加用于配置 host 或 web 应用附加服务的委托。该方法返回当前 <see cref="IWebHostBuilder"/> 实例，可多次调用。
        /// </summary>
        /// <param name="configureServices">A delegate for configuring the <see cref="IServiceCollection"/>.</param>
        /// <returns>The <see cref="IWebHostBuilder"/>.</returns>
        public IWebHostBuilder ConfigureServices(Action<IServiceCollection> configureServices)
        {
            if (configureServices == null)
            {
                throw new ArgumentNullException(nameof(configureServices));
            }

            return ConfigureServices((_, services) => configureServices(services));
        }

        /// <summary>
        /// Adds a delegate for configuring additional services for the host or web application. This may be called
        /// multiple times.
        /// 添加用于配置 host 或 web 应用附加服务的委托。该方法返回当前 <see cref="IWebHostBuilder"/> 实例，可多次调用。
        /// </summary>
        /// <param name="configureServices">A delegate for configuring the <see cref="IServiceCollection"/>.</param>
        /// <returns>The <see cref="IWebHostBuilder"/>.</returns>
        public IWebHostBuilder ConfigureServices(Action<WebHostBuilderContext, IServiceCollection> configureServices)
        {
            // 添加委托
            _configureServices += configureServices;
            return this;
        }

        /// <summary>
        /// Adds a delegate for configuring the <see cref="IConfigurationBuilder"/> that will construct an <see cref="IConfiguration"/>.
        /// 添加用于修改配置的委托
        /// </summary>
        /// <param name="configureDelegate">The delegate for configuring the <see cref="IConfigurationBuilder" /> that will be used to construct an <see cref="IConfiguration" />.</param>
        /// <returns>The <see cref="IWebHostBuilder"/>.</returns>
        /// <remarks>
        /// The <see cref="IConfiguration"/> and <see cref="ILoggerFactory"/> on the <see cref="WebHostBuilderContext"/> are uninitialized at this stage.
        /// The <see cref="IConfigurationBuilder"/> is pre-populated with the settings of the <see cref="IWebHostBuilder"/>.
        /// </remarks>
        public IWebHostBuilder ConfigureAppConfiguration(Action<WebHostBuilderContext, IConfigurationBuilder> configureDelegate)
        {
            _configureAppConfigurationBuilder += configureDelegate;
            return this;
        }

        /// <summary>
        /// Builds the required services and an <see cref="IWebHost"/> which hosts a web application.
        /// 构建所需的服务和托管 web 应用程序的 <see cref="IWebHost"/>。
        /// </summary>
        public IWebHost Build()
        {
            // 只允许 Build 一次
            if (_webHostBuilt)
            {
                throw new InvalidOperationException(Resources.WebHostBuilder_SingleInstance);
            }
            _webHostBuilt = true;

            // 构建公共服务，并实例化 _options
            var hostingServices = BuildCommonServices(out var hostingStartupErrors);
            // 复制一个 hostingServices
            var applicationServices = hostingServices.Clone();
            var hostingServiceProvider = GetProviderFromFactory(hostingServices);

            // 如果未禁用状态信息，检查是否使用了过时的环境变量并输出警告（默认不禁用）
            if (!_options.SuppressStatusMessages)
            {
                // Warn about deprecated environment variables
                if (Environment.GetEnvironmentVariable("Hosting:Environment") != null)
                {
                    Console.WriteLine("The environment variable 'Hosting:Environment' is obsolete and has been replaced with 'ASPNETCORE_ENVIRONMENT'");
                }

                if (Environment.GetEnvironmentVariable("ASPNET_ENV") != null)
                {
                    Console.WriteLine("The environment variable 'ASPNET_ENV' is obsolete and has been replaced with 'ASPNETCORE_ENVIRONMENT'");
                }

                if (Environment.GetEnvironmentVariable("ASPNETCORE_SERVER.URLS") != null)
                {
                    Console.WriteLine("The environment variable 'ASPNETCORE_SERVER.URLS' is obsolete and has been replaced with 'ASPNETCORE_URLS'");
                }
            }

            AddApplicationServices(applicationServices, hostingServiceProvider);

            var host = new WebHost(
                applicationServices,
                hostingServiceProvider,
                _options,
                _config,
                hostingStartupErrors);
            try
            {
                host.Initialize();

                // resolve configuration explicitly once to mark it as resolved within the
                // service provider, ensuring it will be properly disposed with the provider
                _ = host.Services.GetService<IConfiguration>();

                var logger = host.Services.GetRequiredService<ILogger<WebHost>>();

                // Warn about duplicate HostingStartupAssemblies
                foreach (var assemblyName in _options.GetFinalHostingStartupAssemblies().GroupBy(a => a, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                {
                    logger.LogWarning($"The assembly {assemblyName} was specified multiple times. Hosting startup assemblies should only be specified once.");
                }

                return host;
            }
            catch
            {
                // Dispose the host if there's a failure to initialize, this should dispose
                // services that were constructed until the exception was thrown
                host.Dispose();
                throw;
            }

            // C# 7.0 新特性，局部函数
            IServiceProvider GetProviderFromFactory(IServiceCollection collection)
            {
                var provider = collection.BuildServiceProvider();
                var factory = provider.GetService<IServiceProviderFactory<IServiceCollection>>();

                if (factory != null && !(factory is DefaultServiceProviderFactory))
                {
                    using (provider)
                    {
                        return factory.CreateServiceProvider(factory.CreateBuilder(collection));
                    }
                }

                return provider;
            }
        }

        /// <summary>
        /// 构建公共服务
        /// </summary>
        /// <param name="hostingStartupErrors"></param>
        /// <returns></returns>
        [MemberNotNull(nameof(_options))]
        private IServiceCollection BuildCommonServices(out AggregateException? hostingStartupErrors)
        {
            hostingStartupErrors = null;

            // 使用 _config 创建 _options，_options 的属性值基本都来自 _config，详见源码
            _options = new WebHostOptions(_config, Assembly.GetEntryAssembly()?.GetName().Name);

            if (!_options.PreventHostingStartup)
            {
                var exceptions = new List<Exception>();

                // Execute the hosting startup assemblies
                // 执行 hosting startup 程序集
                foreach (var assemblyName in _options.GetFinalHostingStartupAssemblies().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var assembly = Assembly.Load(new AssemblyName(assemblyName));

                        foreach (var attribute in assembly.GetCustomAttributes<HostingStartupAttribute>())
                        {
                            var hostingStartup = (IHostingStartup)Activator.CreateInstance(attribute.HostingStartupType)!;
                            hostingStartup.Configure(this);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Capture any errors that happen during startup
                        exceptions.Add(new InvalidOperationException($"Startup assembly {assemblyName} failed to execute. See the inner exception for more details.", ex));
                    }
                }

                if (exceptions.Count > 0)
                {
                    hostingStartupErrors = new AggregateException(exceptions);
                }
            }

            // 获得 ContentRootPath
            var contentRootPath = ResolveContentRootPath(_options.ContentRootPath, AppContext.BaseDirectory);

            // Initialize the hosting environment
            // 初始化托管环境
            ((IWebHostEnvironment)_hostingEnvironment).Initialize(contentRootPath, _options);
            _context.HostingEnvironment = _hostingEnvironment;

            var services = new ServiceCollection();
            services.AddSingleton(_options);
            services.AddSingleton<IWebHostEnvironment>(_hostingEnvironment);
            services.AddSingleton<IHostEnvironment>(_hostingEnvironment);
#pragma warning disable CS0618 // Type or member is obsolete
            services.AddSingleton<AspNetCore.Hosting.IHostingEnvironment>(_hostingEnvironment);
            services.AddSingleton<Extensions.Hosting.IHostingEnvironment>(_hostingEnvironment);
#pragma warning restore CS0618 // Type or member is obsolete
            services.AddSingleton(_context);

            // 注册 IConfiguration
            var builder = new ConfigurationBuilder()
                .SetBasePath(_hostingEnvironment.ContentRootPath)
                .AddConfiguration(_config, shouldDisposeConfiguration: true);

            _configureAppConfigurationBuilder?.Invoke(_context, builder);

            var configuration = builder.Build();
            // register configuration as factory to make it dispose with the service provider
            services.AddSingleton<IConfiguration>(_ => configuration);
            _context.Configuration = configuration;

            var listener = new DiagnosticListener("Microsoft.AspNetCore");
            services.AddSingleton<DiagnosticListener>(listener);
            services.AddSingleton<DiagnosticSource>(listener);

            services.AddTransient<IApplicationBuilderFactory, ApplicationBuilderFactory>();
            services.AddTransient<IHttpContextFactory, DefaultHttpContextFactory>();
            services.AddScoped<IMiddlewareFactory, MiddlewareFactory>();
            services.AddOptions();
            services.AddLogging();

            services.AddTransient<IServiceProviderFactory<IServiceCollection>, DefaultServiceProviderFactory>();

            if (!string.IsNullOrEmpty(_options.StartupAssembly))
            {
                try
                {
                    var startupType = StartupLoader.FindStartupType(_options.StartupAssembly, _hostingEnvironment.EnvironmentName);

                    if (typeof(IStartup).GetTypeInfo().IsAssignableFrom(startupType.GetTypeInfo()))
                    {
                        services.AddSingleton(typeof(IStartup), startupType);
                    }
                    else
                    {
                        services.AddSingleton(typeof(IStartup), sp =>
                        {
                            var hostingEnvironment = sp.GetRequiredService<IHostEnvironment>();
                            var methods = StartupLoader.LoadMethods(sp, startupType, hostingEnvironment.EnvironmentName);
                            return new ConventionBasedStartup(methods);
                        });
                    }
                }
                catch (Exception ex)
                {
                    var capture = ExceptionDispatchInfo.Capture(ex);
                    services.AddSingleton<IStartup>(_ =>
                    {
                        capture.Throw();
                        return null;
                    });
                }
            }

            _configureServices?.Invoke(_context, services);

            return services;
        }

        private void AddApplicationServices(IServiceCollection services, IServiceProvider hostingServiceProvider)
        {
            // We are forwarding services from hosting container so hosting container
            // can still manage their lifetime (disposal) shared instances with application services.
            // NOTE: This code overrides original services lifetime. Instances would always be singleton in
            // application container.
            var listener = hostingServiceProvider.GetService<DiagnosticListener>();
            services.Replace(ServiceDescriptor.Singleton(typeof(DiagnosticListener), listener!));
            services.Replace(ServiceDescriptor.Singleton(typeof(DiagnosticSource), listener!));
        }

        private string ResolveContentRootPath(string contentRootPath, string basePath)
        {
            if (string.IsNullOrEmpty(contentRootPath))
            {
                return basePath;
            }
            if (Path.IsPathRooted(contentRootPath))
            {
                return contentRootPath;
            }
            return Path.Combine(Path.GetFullPath(basePath), contentRootPath);
        }
    }
}
