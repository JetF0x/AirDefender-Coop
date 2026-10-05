using System.Reflection;
using AirDefender;
using AirDefenderCoop.Commands;
using AirDefenderCoop.Replication;
using HarmonyLib;
using UnityEngine;

namespace AirDefenderCoop.Patches
{
    [HarmonyPatch]
    internal static class ReplicationPatches
    {
        // Client: every contact must come from the host.
        [HarmonyPatch(typeof(ContactRegistry), nameof(ContactRegistry.Register))]
        [HarmonyPostfix]
        private static void AfterRegister(IContact contact)
        {
            if (CoopSession.IsClient) EntityReplicator.ClientOnRegister(contact);
        }

        // Speed / pause buttons and hotkeys on the client ask the host.
        [HarmonyPatch(typeof(TimeControlsUI), nameof(TimeControlsUI.SetTimeScale))]
        [HarmonyPrefix]
        private static bool SetTimeScale(float scale)
        {
            return !CommandRouter.Route("SetTimeScale", w => w.F32(scale));
        }

        // Operator identification from any UI (ID bar, radial menu, flight-plan popup, ASMA).
        // The host performs the full identification (classification, wave propagation, scoring);
        // the client's UI carries on as if it succeeded and the result arrives via track sync.
        [HarmonyPatch(typeof(TrackManager), nameof(TrackManager.TrySetOperatorClassification))]
        [HarmonyPrefix]
        private static bool Identify(string contactId, string identifier, Color color, ref ClassificationApplyResult __result)
        {
            if (!CommandRouter.Route("Identify", w => { w.Str(contactId); w.Str(identifier); w.Color(color); })) return true;
            __result = ClassificationApplyResult.Applied(identifier);
            return false;
        }

        // Consequences of an identification that only the host may evaluate.
        [HarmonyPatch(typeof(IdentificationScoringEvaluator), nameof(IdentificationScoringEvaluator.Evaluate))]
        [HarmonyPrefix]
        private static bool NoClientScoring(string contactId, ref string __result)
        {
            if (!ClientGate.Suppress) return true;
            __result = contactId;
            return false;
        }

        [HarmonyPatch(typeof(TrackManager), nameof(TrackManager.PropagateIdentifierAcrossWave))]
        [HarmonyPrefix]
        private static bool NoClientPropagation() => !ClientGate.Suppress;

        internal static void RegisterCommands()
        {
            CommandRouter.Register("SetTimeScale", r => TimeSync.ApplyTimeScale(r.F32()));
            CommandRouter.Register("Identify", r => HostIdentify(r.Str(), r.Str(), r.Color()));
        }

        /// <summary>The host's equivalent of a player pressing an identity button on a track.</summary>
        private static void HostIdentify(string contactId, string ident, Color color)
        {
            var tm = TrackManager.Instance;
            if (tm == null) return;
            var track = TrackManager.GetRendererForContact(contactId);
            string old = track != null ? track.GetIdentifier() : null;
            var res = tm.TrySetOperatorClassification(contactId, ident, color);
            if (!res.Success)
            {
                CoopLog.Info($"Partner identification of {contactId} as {ident} refused: {res.Message}");
                AsmaService.PostSitrep("IDENT FAILED", $"{CoopSession.PartnerName}: {res.Message}", AsmaMessageSeverity.Advisory);
                return;
            }
            try { ForcePendingTag.ClearPendingRequirement(contactId); } catch { }
            if (ident != "Z") { try { TrackManager.PropagateIdentifierAcrossWave(contactId, ident, color); } catch { } }
            try { IdentificationScoringEvaluator.Evaluate(track, contactId, track != null ? track.GetTrackSerialId() : -1, old, ident, triggerQraOnThreatIdent: true); }
            catch (System.Exception e) { CoopLog.Warn("identify scoring: " + e.Message); }
        }

        /// <summary>Scores are the host's: the client never changes them itself.</summary>
        internal static void ApplyScoringGuards(Harmony h)
        {
            var prefix = new HarmonyMethod(typeof(ReplicationPatches), nameof(SkipOnClient));
            int n = 0;
            foreach (var m in typeof(TriStateScoringService).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.ReturnType != typeof(void)) continue;
                if (!(m.Name.StartsWith("On") || m.Name.StartsWith("Add") || m.Name.StartsWith("Apply"))) continue;
                h.Patch(m, prefix: prefix);
                n++;
            }
            CoopLog.Info($"Scoring guards: {n}");
        }

        private static bool SkipOnClient() => !ClientGate.Suppress;
    }
}
