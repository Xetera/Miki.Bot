using Miki.Bot.Models.Repositories;
using Miki.Discord;
using Miki.Discord.Common;
using Miki.Discord.Rest;
using Miki.Exceptions;
using Miki.Framework;
using Miki.Framework.Events;
using Miki.Framework.Events.Attributes;
using Miki.Framework.Events.Commands;
using Miki.Helpers;
using Miki.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Miki.Modules
{
	[Module(Name = "Marriage")]
	public class MarriageModule
	{
		[Command(Name = "marry")]
		public async Task MarryAsync(EventContext e)
		{
			long askerId = 0;
			long receiverId = 0;

			ArgObject args = e.Arguments.FirstOrDefault();

			if (args == null)
				return;

			IDiscordGuildUser user = await args.GetUserAsync(e.Guild);

			if (user == null)
			{
				e.Channel.QueueMessageAsync("Couldn't find this person..");
				return;
			}

			if (user.Id == (await e.Guild.GetSelfAsync()).Id)
			{
				e.Channel.QueueMessageAsync("(´・ω・`)");
				return;
			}

			using (MikiContext context = new MikiContext())
			{
				MarriageRepository repository = new MarriageRepository(context);

				User mentionedPerson = await User.GetAsync(context, user.Id.ToDbLong(), user.Username);
				User currentUser = await DatabaseHelpers.GetUserAsync(context, e.Author);

				askerId = currentUser.Id;
				receiverId = mentionedPerson.Id;

				if (currentUser == null || mentionedPerson == null)
				{
					e.ErrorEmbed(e.Locale.GetString("miki_module_accounts_marry_error_null")).ToEmbed().QueueToChannel(e.Channel);
					return;
				}

				if (mentionedPerson.Banned)
				{
					e.ErrorEmbed("This person has been banned from Miki.").ToEmbed().QueueToChannel(e.Channel);
					return;
				}

				if (mentionedPerson.Id == currentUser.Id)
				{
					e.ErrorEmbed(e.Locale.GetString("miki_module_accounts_marry_error_null")).ToEmbed().QueueToChannel(e.Channel);
					return;
				}

				if (await repository.ExistsAsync(mentionedPerson.Id, currentUser.Id))
				{
					e.ErrorEmbed(e.Locale.GetString("miki_module_accounts_marry_error_exists")).ToEmbed().QueueToChannel(e.Channel);
					return;
				}

				await repository.ProposeAsync(askerId, receiverId);

				await context.SaveChangesAsync();
			}

			new EmbedBuilder()
				.SetTitle("💍" + e.Locale.GetString("miki_module_accounts_marry_text", $"**{e.Author.Username}**", $"**{user.Username}**"))
				.SetDescription(e.Locale.GetString("miki_module_accounts_marry_text2", user.Username, e.Author.Username))
				.SetColor(0.4f, 0.4f, 0.8f)
				.SetThumbnail("https://i.imgur.com/TKZSKIp.png")
				.AddInlineField("✅ To accept", $">acceptmarriage @user")
				.AddInlineField("❌ To decline", $">declinemarriage @user")
				.SetFooter("Take your time though! This proposal won't disappear", "")
				.ToEmbed().QueueToChannel(e.Channel);
		}

		private async Task ConfirmBuyMarriageSlot(EventContext cont, int costForUpgrade)
		{
			using (var context = new MikiContext())
			{
				User user = await DatabaseHelpers.GetUserAsync(context, cont.Author);

				if (user.Currency >= costForUpgrade)
				{
					user.MarriageSlots++;
					user.Currency -= costForUpgrade;

					new EmbedBuilder()
					{
						Color = new Color(0.4f, 1f, 0.6f),
						Description = cont.Locale.GetString("buymarriageslot_success", user.MarriageSlots),
					}.ToEmbed().QueueToChannel(cont.Channel);

					await context.SaveChangesAsync();

					await cont.EventSystem.GetCommandHandler<SessionBasedCommandHandler>().RemoveSessionAsync(cont.Author.Id, cont.Channel.Id);
				}
				else
				{
					new EmbedBuilder()
					{
						Color = new Color(1, 0.4f, 0.6f),
						Description = cont.Locale.GetString("buymarriageslot_insufficient_mekos", (costForUpgrade - user.Currency)),
					}.ToEmbed().QueueToChannel(cont.Channel);
					await cont.EventSystem.GetCommandHandler<SessionBasedCommandHandler>().RemoveSessionAsync(cont.Author.Id, cont.Channel.Id);
				}
			}
		}

		[Command(Name = "divorce")]
		public async Task DivorceAsync(EventContext e)
		{
			using (MikiContext context = new MikiContext())
			{
				MarriageRepository repository = new MarriageRepository(context);

				ArgObject selection = e.Arguments.FirstOrDefault();
				int? selectionId = null;

				if (selection != null)
				{
					selectionId = selection.TakeInt();
				}

				var marriages = await repository.GetMarriagesAsync((long)e.Author.Id);

				if (marriages.Count == 0)
				{
					// TODO: no proposals exception
					//throw BotException.CreateCustom("error_proposals_empty");
					return;
				}

				marriages = marriages.OrderByDescending(x => x.Marriage.TimeOfMarriage).ToList();

				if (selectionId != null)
				{
					var m = marriages[selectionId.Value - 1];
					var otherUser = await Global.Client.Discord.GetUserAsync(m.GetOther(e.Author.Id.ToDbLong()).FromDbLong());

					new EmbedBuilder
					{
						Title = $"🔔 {e.Locale.GetString("miki_module_accounts_divorce_header")}",
						Description = e.Locale.GetString("miki_module_accounts_divorce_content", e.Author.Username, otherUser.Username),
						Color = new Color(0.6f, 0.4f, 0.1f)
					}.ToEmbed().QueueToChannel(e.Channel);

					m.Remove(context);
					await context.SaveChangesAsync();
				}
				else
				{
					var embed = new EmbedBuilder()
					{
						Title = "💍 Marriages",
						Footer = new EmbedFooter()
						{
							Text = $"Use {await e.Prefix.GetForGuildAsync(Global.RedisClient, e.Guild.Id)}divorce <number> to decline",
						},
						Color = new Color(154, 170, 180)
					};

					await BuildMarriageEmbedAsync(embed, e.Author.Id.ToDbLong(), context, marriages);

					embed.ToEmbed().QueueToChannel(e.Channel);
				}
			}
		}

		[Command(Name = "acceptmarriage")]
		public async Task AcceptMarriageAsync(EventContext e)
		{
			IDiscordUser user = await e.Arguments.Join().GetUserAsync(e.Guild);

			if (user == null)
			{
				e.ErrorEmbed("I couldn't find this user!")
					.ToEmbed().QueueToChannel(e.Channel);
				return;
			}

			if (user.Id == e.Author.Id)
			{
				e.ErrorEmbed("Please mention someone else than yourself.")
					.ToEmbed().QueueToChannel(e.Channel);
				return;
			}

			using (var context = new MikiContext())
			{
				MarriageRepository repository = new MarriageRepository(context);

				User accepter = await DatabaseHelpers.GetUserAsync(context, e.Author);
				User asker = await DatabaseHelpers.GetUserAsync(context, user);

				UserMarriedTo marriage = await repository.GetEntryAsync(accepter.Id, asker.Id);

				if (marriage != null)
				{
					if (accepter.MarriageSlots < (await repository.GetMarriagesAsync(accepter.Id)).Count)
					{
						throw new InsufficientMarriageSlotsException(accepter);
					}

					if (asker.MarriageSlots < (await repository.GetMarriagesAsync(asker.Id)).Count)
					{
						throw new InsufficientMarriageSlotsException(asker);
					}

					if (marriage.ReceiverId != e.Author.Id.ToDbLong())
					{
						e.Channel.QueueMessageAsync($"You can not accept your own responses!");
						return;
					}

					if (marriage.Marriage.IsProposing)
					{
						marriage.Marriage.AcceptProposal();

						await context.SaveChangesAsync();

						new EmbedBuilder()
						{
							Title = ("❤️ Happily married"),
							Color = new Color(190, 25, 49),
							Description = ($"Much love to { e.Author.Username } and { user.Username } in their future adventures together!")
						}.ToEmbed().QueueToChannel(e.Channel);
					}
					else
					{
						e.ErrorEmbed("You're already married to this person ya doofus!")
							.ToEmbed().QueueToChannel(e.Channel);
					}
				}
				else
				{
					e.Channel.QueueMessageAsync("This user hasn't proposed to you!");
					return;
				}
			}
		}

		[Command(Name = "declinemarriage")]
		public async Task DeclineMarriageAsync(EventContext e)
		{
			using (MikiContext context = new MikiContext())
			{
				MarriageRepository repository = new MarriageRepository(context);

				ArgObject selection = e.Arguments.FirstOrDefault();
				int? selectionId = null;

				if (selection != null)
				{
					selectionId = selection.TakeInt();
				}

				var marriages = await repository.GetProposalsReceived(e.Author.Id.ToDbLong());

				if (marriages.Count == 0)
				{
					// TODO: add no propsoals
					//throw new LocalizedException("error_proposals_empty");
					return;
				}

				marriages = marriages.OrderByDescending(x => x.Marriage.TimeOfMarriage).ToList();

				if (selectionId != null)
				{
					var m = marriages[selectionId.Value - 1];
					string otherName = (await Global.Client.Discord.GetUserAsync(m.GetOther(e.Author.Id.ToDbLong()).FromDbLong())).Username;

					new EmbedBuilder()
					{
						Title = $"🔫 You shot down {otherName}!",
						Description = $"Aww, don't worry {otherName}. There is plenty of fish in the sea!",
						Color = new Color(191, 105, 82)
					}.ToEmbed().QueueToChannel(e.Channel);

					m.Remove(context);
					await context.SaveChangesAsync();
				}
				else
				{
					var embed = new EmbedBuilder()
					{
						Title = "💍 Proposals",
						Footer = new EmbedFooter()
						{
							Text = $"Use {await e.Prefix.GetForGuildAsync(Global.RedisClient, e.Guild.Id)}declinemarriage <number> to decline",
						},
						Color = new Color(154, 170, 180)
					};

					await BuildMarriageEmbedAsync(embed, e.Author.Id.ToDbLong(), context, marriages);

					embed.ToEmbed().QueueToChannel(e.Channel);
				}
			}
		}

		[Command(Name = "showproposals")]
		public async Task ShowProposalsAsync(EventContext e)
		{
			int page = e.Arguments.FirstOrDefault()?.TakeInt() - 1 ?? 0;

			using (var context = new MikiContext())
			{
				MarriageRepository repository = new MarriageRepository(context);

				List<UserMarriedTo> proposals = await repository.GetProposalsReceived(e.Author.Id.ToDbLong());
				List<string> proposalNames = new List<string>();

				foreach (UserMarriedTo p in proposals)
				{
					long id = p.GetOther(e.Author.Id.ToDbLong());
					string u = (await Global.Client.Discord.GetUserAsync(id.FromDbLong())).Username;
					proposalNames.Add($"{u} [{id}]");
				}

				int pageCount = (int)Math.Ceiling((float)proposalNames.Count / 35);

				proposalNames = proposalNames.Skip(page * 35)
					.Take(35)
					.ToList();

				EmbedBuilder embed = new EmbedBuilder()
					.SetTitle(e.Author.Username)
					.SetDescription("Here it shows both the people who you've proposed to and who have proposed to you.");

				string output = string.Join("\n", proposalNames);

				embed.AddField("Proposals Recieved", string.IsNullOrEmpty(output) ? "none (yet!)" : output);

				proposals = await repository.GetProposalsSent(e.Author.Id.ToDbLong());
				proposalNames = new List<string>();

				foreach (UserMarriedTo p in proposals)
				{
					long id = p.GetOther(e.Author.Id.ToDbLong());
					string u = (await Global.Client.Discord.GetUserAsync(id.FromDbLong())).Username;
					proposalNames.Add($"{u} [{id}]");
				}

				pageCount = Math.Max(pageCount, (int)Math.Ceiling((float)proposalNames.Count / 35));

				proposalNames = proposalNames.Skip(page * 35)
					.Take(35)
					.ToList();

				output = string.Join("\n", proposalNames);

				embed.AddField("Proposals Sent", string.IsNullOrEmpty(output) ? "none (yet!)" : output);

				embed.Color = new Color(1, 0.5f, 0);
				embed.ThumbnailUrl = (await e.Guild.GetMemberAsync(e.Author.Id)).GetAvatarUrl();
				if (pageCount > 1)
				{
					embed.SetFooter(e.Locale.GetString("page_footer", page + 1, pageCount));
				}
				embed.ToEmbed().QueueToChannel(e.Channel);
			}
		}

		[Command(Name = "buymarriageslot")]
		public async Task BuyMarriageSlotAsync(EventContext e)
		{
			using (var context = new MikiContext())
			{
				User user = await DatabaseHelpers.GetUserAsync(context, e.Author);

				int limit = 10;
				bool isDonator = await user.IsDonatorAsync(context);

				if (isDonator)
				{
					limit += 5;
				}

				EmbedBuilder embed = new EmbedBuilder();

				if (user.MarriageSlots >= limit)
				{
					embed.Description = $"For now, **{limit} slots** is the max. sorry :(";

					if (limit == 10 && !isDonator)
					{
						embed.AddField("Pro tip!", "Donators get 5 more slots!")
							.SetFooter("Check `>donate` for more information!", "");
					}

					embed.Color = new Color(1f, 0.6f, 0.4f);
					embed.ToEmbed().QueueToChannel(e.Channel);
					return;
				}

				int costForUpgrade = (user.MarriageSlots - 4) * 2500;

				await ConfirmBuyMarriageSlot(e, costForUpgrade);
			}
		}

		private async Task<EmbedBuilder> BuildMarriageEmbedAsync(EmbedBuilder embed, long userId, MikiContext context, List<UserMarriedTo> marriages)
		{
			StringBuilder builder = new StringBuilder();

			for (int i = 0; i < marriages.Count; i++)
			{
				builder.AppendLine($"`{(i + 1).ToString().PadLeft(2)}:` {(await Global.Client.Discord.GetUserAsync(marriages[i].GetOther(userId).FromDbLong())).Username}");
			}

			embed.Description += "\n\n" + builder.ToString();

			return embed;
		}
	}
}