using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TBot.Common.Logging;
using Tbot.Helpers;
using TBot.Model;
using Tbot.Services;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Tbot.Includes;
using System.Timers;
using TBot.Ogame.Infrastructure;
using Tbot.Common.Settings;

namespace Tbot.Workers {
	internal class DefenderWorker : WorkerBase {
		private readonly IFleetScheduler _fleetScheduler;
		private readonly IOgameService _ogameService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		public DefenderWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ITBotOgamedBridge tbotOgameBridge)
			: base(parentInstance) {
			_fleetScheduler = fleetScheduler;
			_ogameService = ogameService;
			_tbotOgameBridge = tbotOgameBridge;
		}

		protected override async Task Execute() {
			try {
				DoLog(LogLevel.Information, "Checking attacks...");

				await FakeActivity();
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				bool isUnderAttack = await _ogameService.IsUnderAttack();
				DateTime time = await _tbotOgameBridge.GetDateTime();
				if (isUnderAttack) {
					if ((bool) _tbotInstance.InstanceSettings.Defender.Alarm.Active)
						await Task.Factory.StartNew(() => ConsoleHelpers.PlayAlarm(), _ct);
					// UpdateTitle(false, true);
					DoLog(LogLevel.Warning, "ENEMY ACTIVITY!!!");
					_tbotInstance.UserData.attacks = await _ogameService.GetAttacks();
					foreach (AttackerFleet attack in _tbotInstance.UserData.attacks) {
						HandleAttack(attack);
					}
				} else {
					DoLog(LogLevel.Information, "Your empire is safe");
				}
				long interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Defender.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Defender.CheckIntervalMax);
				if (interval <= 0)
					interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);

				// Display dateTime for logging 
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
				await _tbotOgameBridge.CheckCelestials();
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"An error has occurred while checking for attacks: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				DateTime time = await _tbotOgameBridge.GetDateTime();
				long interval = RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds);
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
				await _tbotOgameBridge.CheckCelestials();
			} finally {

			}
		}
		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool) _tbotInstance.InstanceSettings.Defender.Active;
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "Defender";
		}
		public override Feature GetFeature() {
			return Feature.Defender;
		}

		public override LogSender GetLogSender() {
			return LogSender.Defender;
		}


		private async Task FakeActivity() {
			//checking if under attack by making activity on planet/moon configured in settings (otherwise make acti on latest activated planet)
			// And make activity on one more random planet to fake real player
			Celestial celestial;
			Celestial randomCelestial;
			celestial = _tbotInstance.UserData.celestials
				.Unique()
				.Where(c => c.Coordinate.Galaxy == (int) _tbotInstance.InstanceSettings.Brain.Transports.Origin.Galaxy)
				.Where(c => c.Coordinate.System == (int) _tbotInstance.InstanceSettings.Brain.Transports.Origin.System)
				.Where(c => c.Coordinate.Position == (int) _tbotInstance.InstanceSettings.Brain.Transports.Origin.Position)
				.Where(c => c.Coordinate.Type == Enum.Parse<Celestials>((string) _tbotInstance.InstanceSettings.Brain.Transports.Origin.Type))
				.SingleOrDefault() ?? new() { ID = 0 };

			if (celestial.ID != 0) {
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Defences);
			}
			randomCelestial = _tbotInstance.UserData.celestials.Shuffle().FirstOrDefault() ?? new() { ID = 0 };
			if (randomCelestial.ID != 0) {
				randomCelestial = await _tbotOgameBridge.UpdatePlanet(randomCelestial, UpdateTypes.Defences);
			}

			return;
		}

		private async void HandleAttack(AttackerFleet attack) {
			if (_tbotInstance.UserData.celestials.Count() == 0) {
				DateTime time = await _tbotOgameBridge.GetDateTime();
				long interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Warning, "Unable to handle attack at the moment: bot is still getting account info.");
				DoLog(LogLevel.Information,  $"Next check at {newTime.ToString()}");
				return;
			}

			Celestial attackedCelestial = _tbotInstance.UserData.celestials.Unique().SingleOrDefault(planet => planet.HasCoords(attack.Destination));
			attackedCelestial = await _tbotOgameBridge.UpdatePlanet(attackedCelestial, UpdateTypes.Ships);

			try {
				if ((_tbotInstance.InstanceSettings.Defender.WhiteList as long[]).Any()) {
					foreach (int playerID in (long[]) _tbotInstance.InstanceSettings.Defender.WhiteList) {
						if (attack.AttackerID == playerID) {
							DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: attacker {attack.AttackerName} whitelisted.");
							return;
						}
					}
				}
			} catch {
				DoLog(LogLevel.Warning, "An error has occurred while checking Defender WhiteList");
			}

			try {
				if (attack.MissionType == Missions.MissileAttack) {
					if (
						!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "IgnoreMissiles") ||
						(SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "IgnoreMissiles") && (bool) _tbotInstance.InstanceSettings.Defender.IgnoreMissiles)
					) {
						DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: missiles attack.");
						return;
					}
				}
				if (attack.Ships != null && _tbotInstance.UserData.researches.EspionageTechnology >= 8) {
					if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "IgnoreProbes") && (bool) _tbotInstance.InstanceSettings.Defender.IgnoreProbes && attack.IsOnlyProbes()) {
						if (attack.MissionType == Missions.Spy)
							DoLog(LogLevel.Information, "Attacker sent only Probes! Espionage action skipped.");
						else
							DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: only Espionage Probes.");

						return;
					}
					if (
						(bool) _tbotInstance.InstanceSettings.Defender.IgnoreWeakAttack &&
						attack.Ships.GetFleetPoints() < (attackedCelestial.Ships.GetFleetPoints() / (int) _tbotInstance.InstanceSettings.Defender.WeakAttackRatio)
					) {
						DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: weak attack.");
						return;
					}
				} else {
					DoLog(LogLevel.Information, "Unable to detect fleet composition.");
				}
			} catch {
				DoLog(LogLevel.Warning, "An error has occurred while checking attacker fleet composition");
			}

			if ((bool) _tbotInstance.InstanceSettings.Defender.TelegramMessenger.Active) {
				await _tbotInstance.SendTelegramMessage($"Player {attack.AttackerName} ({attack.AttackerID}) is attacking your planet {attack.Destination.ToString()} arriving at {attack.ArrivalTime.ToString()}");
				if (attack.Ships != null)
					await Task.Delay(1000, _ct);
				await _tbotInstance.SendTelegramMessage($"The attack is composed by: {attack.Ships.ToString()}");
			}
			DoLog(LogLevel.Warning, $"Player {attack.AttackerName} ({attack.AttackerID}) is attacking your planet {attackedCelestial.ToString()} arriving at {attack.ArrivalTime.ToString()}");
			if (attack.Ships != null)
				await Task.Delay(1000, _ct);
			DoLog(LogLevel.Warning, $"The attack is composed by: {attack.Ships.ToString()}");

			if ((bool) _tbotInstance.InstanceSettings.Defender.SpyAttacker.Active) {
				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
				if (attackedCelestial.Ships.EspionageProbe == 0) {
					DoLog(LogLevel.Warning, "Could not spy attacker: no probes available.");
				} else {
					try {
						Coordinate destination = attack.Origin;
						Ships ships = new() { EspionageProbe = (int) _tbotInstance.InstanceSettings.Defender.SpyAttacker.Probes };
						int fleetId = await _fleetScheduler.SendFleet(attackedCelestial, ships, destination, Missions.Spy, Speeds.HundredPercent, new Resources(), _tbotInstance.UserData.userInfo.Class);
						Fleet fleet = _tbotInstance.UserData.fleets.Single(fleet => fleet.ID == fleetId);
						DoLog(LogLevel.Information, $"Spying attacker from {attackedCelestial.ToString()} to {destination.ToString()} with {_tbotInstance.InstanceSettings.Defender.SpyAttacker.Probes} probes. Arrival at {fleet.ArrivalTime.ToString()}");
					} catch (Exception e) {
						DoLog(LogLevel.Error, $"Could not spy attacker: an exception has occurred: {e.Message}");
						DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
					}
				}
			}

			if ((bool) _tbotInstance.InstanceSettings.Defender.MessageAttacker.Active) {
				try {
					if (attack.AttackerID != 0) {
						Random random = new();
						string[] messages = _tbotInstance.InstanceSettings.Defender.MessageAttacker.Messages;
						string message = messages.ToList().Shuffle().First();
						DoLog(LogLevel.Information, $"Sending message \"{message}\" to attacker {attack.AttackerName}");
						try {
							await _ogameService.SendMessage(attack.AttackerID, message);
							DoLog(LogLevel.Information, "Message succesfully sent.");
						} catch {
							DoLog(LogLevel.Warning, "Unable send message.");
						}
					} else {
						DoLog(LogLevel.Warning, "Unable send message.");
					}

				} catch (Exception e) {
					DoLog(LogLevel.Error, $"Could not message attacker: an exception has occurred: {e.Message}");
					DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				}
			}

			if ((bool) _tbotInstance.InstanceSettings.Defender.Autofleet.Active) {
				try {
					var minFlightTime = attack.ArriveIn + (attack.ArriveIn / 100 * 30) + (RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds) / 1000);
					await _fleetScheduler.AutoFleetSave(attackedCelestial, false, minFlightTime, true);
				} catch (Exception e) {
					DoLog(LogLevel.Error, $"Could not fleetsave: an exception has occurred: {e.Message}");
					DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				}
			}
		}
	}
}
