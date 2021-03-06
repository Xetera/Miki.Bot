﻿using Microsoft.EntityFrameworkCore;
using Miki.Cache.StackExchange;
using Miki.Common;
using Miki.Configuration;
using Miki.Discord;
using Miki.Discord.Caching.Stages;
using Miki.Discord.Common;
using Miki.Discord.Gateway.Distributed;
using Miki.Discord.Rest;
using Miki.Framework;
using Miki.Framework.Events;
using Miki.Framework.Events.Commands;
using Miki.Framework.Events.Filters;
using Miki.Framework.Languages;
using Miki.Framework.Services;
using Miki.Localization.Exceptions;
using Miki.Logging;
using Miki.Models;
using Miki.Models.Objects.Backgrounds;
using Miki.Serialization.Protobuf;
using SharpRaven.Data;
using StackExchange.Redis;
using StatsdClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Threading.Tasks;

namespace Miki
{
    public class Program
	{
		private IGateway _gateway;

		private static async Task Main()
		{
			Program p = new Program();

			Global.RedisClient = new StackExchangeCacheClient(
				new ProtobufSerializer(),
				await ConnectionMultiplexer.ConnectAsync(Global.Config.RedisConnectionString)
			);

			await p.LoadDiscord();

			p.LoadLocales();

			Global.Backgrounds = new BackgroundStore();

			for (int i = 0; i < Global.Config.MessageWorkerCount; i++)
			{
				MessageBucket.AddWorker();
			}

			using (var c = new MikiContext())
			{
				List<User> bannedUsers = await c.Users.Where(x => x.Banned).ToListAsync();
				foreach (var u in bannedUsers)
				{
					Global.Client.GetAttachedObject<EventSystem>().MessageFilter
						.Get<UserFilter>().Users.Add(u.Id.FromDbLong());
				}
			}

			await Task.Delay(-1);
		}

		private void LoadLocales()
		{
			string nameSpace = "Miki.Languages";

			var typeList = Assembly.GetExecutingAssembly()
				.GetTypes()
				.Where(t => t.IsClass && t.Namespace == nameSpace);

			typeList.ToList()
				.ForEach(t =>
			{
				try
				{
					string l = t.Name.ToLowerInvariant();

					ResourceManager resources = new ResourceManager($"Miki.Languages.{l}", t.Assembly);
					Locale.LoadLanguage(l, resources, resources.GetString("current_language_name"));
				}
				catch (Exception ex)
				{
					Log.Error($"Language {t.Name} did not load correctly");
					Log.Debug(ex.ToString());
				}
			});

			Locale.SetDefaultLanguage("eng");
		}

		public async Task LoadDiscord()
		{
			Global.ApiClient = new DiscordApiClient(Global.Config.Token, Global.RedisClient);

			if (Global.Config.SelfHosted)
			{
				//var gatewayConfig = GatewayConfiguration.Default();
				//gatewayConfig.ShardCount = 1;
				//gatewayConfig.ShardId = 0;
				//gatewayConfig.Token = Global.Config.Token;
				//gatewayConfig.ApiClient = Global.ApiClient;
				//gatewayConfig.WebSocketClient = new BasicWebSocketClient();
				//Global.Gateway = new CentralizedGatewayShard(gatewayConfig);
			}
			else
			{
				// For distributed systems
				_gateway = new DistributedGateway(new MessageClientConfiguration
				{
					ConnectionString = new Uri(Global.Config.RabbitUrl.ToString()),
					QueueName = "gateway",
					ExchangeName = "consumer",
					ConsumerAutoAck = false,
					PrefetchCount = 25
				});
			}

			Global.Client = new MikiApplication(new ClientInformation()
			{
				ClientConfiguration = new DiscordClientConfigurations
				{
					ApiClient = Global.ApiClient,
					CacheClient = Global.RedisClient,
					Gateway = _gateway
				},
				DatabaseContextFactory = () => new MikiContext()
			});

			var logging = new LoggingService();
			Global.Client.AddService(logging);

			new BasicCacheStage().Initialize(_gateway, Global.RedisClient);

			Global.ApiClient.RestClient.OnRequestComplete += (method, uri) =>
			{
				Log.Debug(method + " " + uri);
				DogStatsd.Histogram("discord.http.requests", uri, 1, new string[] { $"http_method:{method}" });
			};

			Global.CurrentUser = await Global.Client.Discord.GetCurrentUserAsync();

			EventSystem eventSystem = new EventSystem(new EventSystemConfig()
			{
				Developers = Global.Config.DeveloperIds,
			});

			eventSystem.OnError += async (ex, context) =>
			{
				if (ex is LocalizedException botEx)
				{
					Utils.ErrorEmbedResource(context, botEx.LocaleResource)
						.ToEmbed().QueueToChannel(context.Channel);
				}
				else
				{
					Log.Error(ex);
					await Global.ravenClient.CaptureAsync(new SentryEvent(ex));
				}
			};

			eventSystem.MessageFilter.AddFilter(new BotFilter());
			eventSystem.MessageFilter.AddFilter(new UserFilter());

			Global.Client.Attach(eventSystem);
			ConfigurationManager mg = new ConfigurationManager();

			var commandMap = new Framework.Events.CommandMap();
			commandMap.OnModuleLoaded += (module) =>
			{
				mg.RegisterType(module.GetReflectedInstance().GetType(), module.GetReflectedInstance());
			};

			var handler = new SimpleCommandHandler(Global.RedisClient, commandMap);

			handler.AddPrefix(">", true, true);
			handler.AddPrefix("miki.");

			var sessionHandler = new SessionBasedCommandHandler(Global.RedisClient);
			var messageHandler = new MessageListener(Global.RedisClient);

			eventSystem.AddCommandHandler(sessionHandler);
			eventSystem.AddCommandHandler(messageHandler);
			eventSystem.AddCommandHandler(handler);

			commandMap.RegisterAttributeCommands();
			commandMap.Install(eventSystem);

			string configFile = Environment.CurrentDirectory + Config.MikiConfigurationFile;

			if (File.Exists(configFile))
			{
				await mg.ImportAsync(
					new JsonSerializationProvider(),
					configFile
				);
			}

			await mg.ExportAsync(
				new JsonSerializationProvider(),
				configFile
			);

			if (!string.IsNullOrWhiteSpace(Global.Config.SharpRavenKey))
			{
				Global.ravenClient = new SharpRaven.RavenClient(Global.Config.SharpRavenKey);
			}

			handler.OnMessageProcessed += async (cmd, msg, time) =>
			{
				await Task.Yield();
				Log.Message($"{cmd.Name} processed in {time}ms");
			};

			Global.Client.Discord.MessageCreate += Bot_MessageReceived;

			Global.Client.Discord.GuildJoin += Client_JoinedGuild;
			Global.Client.Discord.GuildLeave += Client_LeftGuild;
			Global.Client.Discord.UserUpdate += Client_UserUpdated;

			await _gateway.StartAsync();
		}

		private async Task Client_UserUpdated(IDiscordUser oldUser, IDiscordUser newUser)
		{
			if (oldUser.AvatarId != newUser.AvatarId)
			{
				await Utils.SyncAvatarAsync(newUser);
			}
		}

		private async Task Bot_MessageReceived(IDiscordMessage arg)
		{
			DogStatsd.Increment("messages.received");

			if (arg.Content.StartsWith($"<@!{Global.CurrentUser.Id}>") || arg.Content.StartsWith($"<@{Global.CurrentUser.Id}>"))
			{
				string msg = (await Locale.GetLanguageInstanceAsync(arg.ChannelId)).GetString("miki_join_message");
				(await arg.GetChannelAsync()).QueueMessageAsync(msg);
			}
		}

		private Task Client_LeftGuild(ulong guildId)
		{
			DogStatsd.Increment("guilds.left");
			return Task.CompletedTask;
		}

		private async Task Client_JoinedGuild(IDiscordGuild arg)
		{
			IDiscordChannel defaultChannel = await arg.GetDefaultChannelAsync();

			if (defaultChannel != null)
			{
				LocaleInstance i = await Locale.GetLanguageInstanceAsync(defaultChannel.Id);
				(defaultChannel as IDiscordTextChannel).QueueMessageAsync(i.GetString("miki_join_message"));
			}

			//List<string> allArgs = new List<string>();
			//List<object> allParams = new List<object>();
			//List<object> allExpParams = new List<object>();

			//try
			//{
			//	for (int i = 0; i < arg.Members.Count; i++)
			//	{
			//		allArgs.Add($"(@p{i * 2}, @p{i * 2 + 1})");

			//		allParams.Add(arg.Members.ElementAt(i).Id.ToDbLong());
			//		allParams.Add(arg.Members.ElementAt(i).Username);

			//		allExpParams.Add(arg.Id.ToDbLong());
			//		allExpParams.Add(arg.Members.ElementAt(i).Id.ToDbLong());
			//	}

			//	using (var context = new MikiContext())
			//	{
			//		await context.Database.ExecuteSqlCommandAsync(
			//			$"INSERT INTO dbo.\"Users\" (\"Id\", \"Name\") VALUES {string.Join(",", allArgs)} ON CONFLICT DO NOTHING", allParams);

			//		await context.Database.ExecuteSqlCommandAsync(
			//			$"INSERT INTO dbo.\"LocalExperience\" (\"ServerId\", \"UserId\") VALUES {string.Join(",", allArgs)} ON CONFLICT DO NOTHING", allExpParams);

			//		await context.SaveChangesAsync();
			//	}
			//}
			//catch (Exception e)
			//{
			//	Log.Error(e.ToString());
			//}

			DogStatsd.Increment("guilds.joined");
		}
	}
}