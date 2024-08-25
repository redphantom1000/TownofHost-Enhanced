﻿using AmongUs.GameOptions;
using Hazel;
using System;
using System.Runtime.CompilerServices;
using TOHE.Modules;
using TOHE.Roles.Core;
using static TOHE.SelectRolesPatch;

namespace TOHE;

public static class AntiBlackout
{
    ///<summary>
    /// Check num alive Impostors & Crewmates & NeutralKillers
    ///</summary>
    public static bool BlackOutIsActive => false; /*!Options.DisableAntiBlackoutProtects.GetBool() && CheckBlackOut();*/

    //this does much less drastic things,
    //and exist cuz even with better antiblackout protect, some places people still blackout
    //(But it is MUCH less than before, and even if they do, after the next meeting they are back to normal 100%)
    public static bool LesserBlackOutActive => CheckBlackOut();
    public static int ExilePlayerId = -1;

    ///<summary>
    /// Count alive players and check black out 
    ///</summary>
    public static bool CheckBlackOut()
    {
        HashSet<byte> Impostors = [];
        HashSet<byte> Crewmates = [];
        HashSet<byte> NeutralKillers = [];

        var lastExiled = ExileControllerWrapUpPatch.AntiBlackout_LastExiled;
        foreach (var pc in Main.AllAlivePlayerControls)
        {
            // if player is ejected, do not count him as alive
            if (lastExiled != null && pc.PlayerId == lastExiled.PlayerId) continue;

            // Impostors
            if (pc.Is(Custom_Team.Impostor))
                Impostors.Add(pc.PlayerId);

            // Only Neutral killers
            else if (pc.IsNeutralKiller() || pc.IsNeutralApocalypse()) 
                NeutralKillers.Add(pc.PlayerId);

            // Crewmate
            else Crewmates.Add(pc.PlayerId);
        }

         var numAliveImpostors = Impostors.Count;
        var numAliveCrewmates = Crewmates.Count;
        var numAliveNeutralKillers = NeutralKillers.Count;

        Logger.Info($" {numAliveImpostors}", "AntiBlackout Num Alive Impostors");
        Logger.Info($" {numAliveCrewmates}", "AntiBlackout Num Alive Crewmates");
        Logger.Info($" {numAliveNeutralKillers}", "AntiBlackout Num Alive Neutral Killers");

        var BlackOutIsActive = false;

        // All real imposotrs is dead
        if (!BlackOutIsActive)
            BlackOutIsActive = numAliveImpostors <= 0;

        // Alive Impostors > or = others team count
        if (!BlackOutIsActive)
            BlackOutIsActive = (numAliveNeutralKillers + numAliveCrewmates) <= numAliveImpostors;

        // One Impostor and one Neutral Killer is alive, and living Crewmates very few
        if (!BlackOutIsActive)
            BlackOutIsActive = numAliveNeutralKillers == 1 && numAliveImpostors == 1 && numAliveCrewmates <= 2;

        Logger.Info($" {BlackOutIsActive}", "BlackOut Is Active");
        return BlackOutIsActive;
    }

    public static bool IsCached { get; private set; } = false;
    private static Dictionary<byte, (bool isDead, bool Disconnected)> isDeadCache = [];
    private readonly static LogHandler logger = Logger.Handler("AntiBlackout");

    public static void SetIsDead(bool doSend = true, [CallerMemberName] string callerMethodName = "")
    {
        TempRevivePlayers();
        logger.Info($"SetIsDead is called from {callerMethodName}");
        if (IsCached)
        {
            logger.Info("Please run RestoreIsDead before running SetIsDead again.");
            return;
        }
        isDeadCache.Clear();
        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null) continue;
            isDeadCache[info.PlayerId] = (info.IsDead, info.Disconnected);
            info.IsDead = false;
            info.Disconnected = false;
        }
        IsCached = true;
        if (doSend) SendGameData();
    }
    public static void RestoreIsDead(bool doSend = true, [CallerMemberName] string callerMethodName = "")
    {

        logger.Info($"RestoreIsDead is called from {callerMethodName}");
        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null) continue;
            if (isDeadCache.TryGetValue(info.PlayerId, out var val))
            {
                info.IsDead = val.isDead;
                info.Disconnected = val.Disconnected;
            }
        }
        isDeadCache.Clear();
        IsCached = false;
        if (doSend) SendGameData();
    }

    public static void SendGameData([CallerMemberName] string callerMethodName = "")
    {
        logger.Info($"SendGameData is called from {callerMethodName}");
        foreach (var playerinfo in GameData.Instance.AllPlayers)
        {
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(5); //0x05 GameData
            {
                writer.Write(AmongUsClient.Instance.GameId);
                writer.StartMessage(1); //0x01 Data
                {
                    writer.WritePacked(playerinfo.NetId);
                    playerinfo.Serialize(writer, true);
                }
                writer.EndMessage();
            }
            writer.EndMessage();

            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
        }
    }
    public static void OnDisconnect(NetworkedPlayerInfo player)
    {
        // Execution conditions: Client is the host, IsDead is overridden, player is already disconnected
        if (!AmongUsClient.Instance.AmHost || !IsCached || !player.Disconnected) return;
        isDeadCache[player.PlayerId] = (true, true);
        player.IsDead = player.Disconnected = false;
        SendGameData();
    }

    ///<summary>
    ///Execute the code with IsDead temporarily set back to what it should be
    ///<param name="action">Execution details</param>
    ///</summary>
    public static void TempRestore(Action action)
    {
        logger.Info("==Temp Restore==");
        // Whether TempRestore was executed with IsDead overwritten
        bool before_IsCached = IsCached;
        try
        {
            if (before_IsCached) RestoreIsDead(doSend: false);
            action();
        }
        catch (Exception ex)
        {
            logger.Warn("An exception occurred within AntiBlackout.TempRestore");
            logger.Exception(ex);
        }
        finally
        {
            if (before_IsCached) SetIsDead(doSend: false);
            logger.Info("==/Temp Restore==");
        }
    }
    public static void AntiBlackRpcVotingComplete(this MeetingHud __instance, MeetingHud.VoterState[] states, NetworkedPlayerInfo exiled, bool tie)
    {
        if (AmongUsClient.Instance.AmClient)
        {
            __instance.VotingComplete(states, exiled, tie);
        }

        var sender = CustomRpcSender.Create("AntiBlack RpcVotingComplete", SendOption.None);
        foreach (var pc in Main.AllPlayerControls)
        {
            if (pc.AmOwner) continue;
            if (pc.IsModClient()) //For mod client show real result
            {
                sender.AutoStartRpc(__instance.NetId, (byte)RpcCalls.VotingComplete, pc.GetClientId());
                {
                    sender.WritePacked(states.Length);
                    foreach (MeetingHud.VoterState voterState in states)
                    {
                        sender.WriteMessageType(voterState.VoterId);
                        sender.Write(voterState.VotedForId);
                        sender.WriteEndMessage();
                    }
                    sender.Write(exiled != null ? exiled.PlayerId : byte.MaxValue);
                    sender.Write(tie);
                    sender.EndRpc();
                }
            }
            else //For vanilla client show a tie
            {
                sender.AutoStartRpc(__instance.NetId, (byte)RpcCalls.VotingComplete, pc.GetClientId());
                {
                    sender.WritePacked(states.Length);
                    foreach (MeetingHud.VoterState voterState in states)
                    {
                        sender.WriteMessageType(voterState.VoterId);
                        sender.Write(voterState.VotedForId);
                        sender.WriteEndMessage();
                    }
                    sender.Write(byte.MaxValue);
                    sender.Write(true);
                    sender.EndRpc();
                }
            }
        }
        sender.SendMessage();
    }
    public static void AfterMeetingTasks()
    {
        var timeNotify = 0f;

        if (CheckForEndVotingPatch.TempExileMsg != null && BlackOutIsActive)
        {
            timeNotify = 4f;
            foreach (var pc in Main.AllPlayerControls.Where(p => p != null && !(p.AmOwner || p.IsModClient())).ToArray())
            {
                pc.Notify(CheckForEndVotingPatch.TempExileMsg, time: timeNotify);
            }
        }

        try
        {
            _ = new LateTask(() =>
            {
                foreach (var pc in Main.AllAlivePlayerControls)
                {
                    pc.GetRoleClass()?.NotifyAfterMeeting();
                }
            }, timeNotify + 0.2f, "Notify AfterMeetingTasks");
        }
        catch (Exception error)
        {
            Logger.Error($"{error}", "AntiBlackout.AfterMeetingTasks");
        }
    }
    public static void ResetPlayerMaps()
    {
        if (CustomWinnerHolder.WinnerTeam != CustomWinner.Default) return;

        foreach (var ((seer, target), (roletype, _)) in RpcSetRoleReplacer.RoleMap)
        {
            if (seer.OwnedByHost()) continue;

            var realtype = roletype;
            if (target.Data.IsDead)
            {
                realtype = seer.HasKillButton() && seer.CanUseSabotage() ? RoleTypes.ImpostorGhost : RoleTypes.CrewmateGhost;
            }
            target.RpcSetRoleDesync(realtype, seer.GetClientId());
        }
        _ = new LateTask(() => {
            foreach (var seer in Main.AllPlayerControls.Where(x => x.GetRoleClass().ThisRoleBase == CustomRoles.GuardianAngel))
            {
                seer.RpcSetRoleDesync(RoleTypes.GuardianAngel, seer.GetClientId());
            } 

            foreach (var target in Main.AllPlayerControls)
            {
                foreach (var seer in Main.AllPlayerControls.Where(x => x.Data.IsDead)) // fix not being able to go trough walls
                {
                    if (seer.OwnedByHost()) continue;
                    MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(target.NetId, (byte)RpcCalls.Exiled, SendOption.None, seer.GetClientId());
                    AmongUsClient.Instance.FinishRpcImmediately(writer);
                }
            }
            

        }, 0.5f, "AntiBlackout - Fix Movement For Ghosts"); 
    }
    private static void TempRevivePlayers()
    {
        if (CustomWinnerHolder.WinnerTeam != CustomWinner.Default) return;

        PlayerControl dummyImp = Main.AllAlivePlayerControls.FirstOrDefault(x => x.PlayerId != ExilePlayerId && x.HasKillButton())
            ?? Main.AllAlivePlayerControls.FirstOrDefault(x => x.PlayerId != ExilePlayerId);

        foreach (var pc in Main.AllPlayerControls)
        {
            foreach (var reciever in Main.AllPlayerControls)
            {
                if (reciever.OwnedByHost()) continue;
                RoleTypes typa = pc == dummyImp ? RoleTypes.Impostor : RoleTypes.Crewmate;

                pc.RpcSetRoleDesync(typa, reciever.GetClientId());
            }
        }
        
        ExilePlayerId = -1;
    }
    public static void Reset()
    {
        logger.Info("==Reset==");
        isDeadCache ??= [];
        isDeadCache.Clear();
        IsCached = false;
        ShowExiledInfo = false;
        StoreExiledMessage = "";
    }

    public static bool ShowExiledInfo = false;
    public static string StoreExiledMessage = "";
}