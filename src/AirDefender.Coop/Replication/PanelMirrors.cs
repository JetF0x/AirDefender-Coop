using System;
using System.Collections.Generic;
using AirDefender;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// Bookkeeping that only the host's simulation writes but every player's panels read: pending
    /// QRA launches, supply flights and the radio channel list. Sent as JSON by CustomGlobals and
    /// written into the client's existing collections in place (they are static readonly or keep
    /// case-insensitive comparers, so they must not be replaced).
    /// </summary>
    internal static class PanelMirrors
    {
        // ------------------------------------------------------------ pending QRA launches

        [Serializable]
        private sealed class PendingQraState
        {
            public List<string> pending = new List<string>();
            public List<string> launched = new List<string>();
            public List<string> cancelled = new List<string>();
            public List<string> sorties = new List<string>();
            public List<string> countKeys = new List<string>();
            public List<int> counts = new List<int>();
            public List<string> sortieKeys = new List<string>();
            public List<string> sortieValues = new List<string>();
            public List<string> launchKeys = new List<string>();
            public List<string> launchValues = new List<string>();
            public List<string> etaKeys = new List<string>();
            public List<float> etaValues = new List<float>();   // timeline seconds, or seconds remaining
            public List<bool> etaTimeline = new List<bool>();
        }

        public static string CapturePendingQra()
        {
            var s = new PendingQraState();
            s.pending.AddRange(QRAQuickLaunchService.s_pendingOrProcessingTargets);
            s.launched.AddRange(QRAQuickLaunchService.s_targetsLaunched);
            s.cancelled.AddRange(QRAQuickLaunchService.s_cancelledTargets);
            s.sorties.AddRange(QRAQuickLaunchService.s_pendingSorties);
            Split(QRAQuickLaunchService.s_desiredLaunchCountByTarget, s.countKeys, s.counts);
            Split(QRAQuickLaunchService.s_pendingSortieByTarget, s.sortieKeys, s.sortieValues);
            Split(QRAQuickLaunchService.s_launchSortieByInterceptorContact, s.launchKeys, s.launchValues);
            foreach (var kv in QRAQuickLaunchService.s_pendingLaunchInfoByTarget)
            {
                s.etaKeys.Add(kv.Key);
                // Unscaled ETAs are on this process's own clock; send what is left instead.
                s.etaValues.Add(kv.Value.usesTimeline ? kv.Value.launchAt : Mathf.Round(kv.Value.launchAt - Time.unscaledTime));
                s.etaTimeline.Add(kv.Value.usesTimeline);
            }
            return JsonUtility.ToJson(s);
        }

        public static void ApplyPendingQra(string json)
        {
            var s = JsonUtility.FromJson<PendingQraState>(json);
            Fill(QRAQuickLaunchService.s_pendingOrProcessingTargets, s.pending);
            Fill(QRAQuickLaunchService.s_targetsLaunched, s.launched);
            Fill(QRAQuickLaunchService.s_cancelledTargets, s.cancelled);
            Fill(QRAQuickLaunchService.s_pendingSorties, s.sorties);
            Fill(QRAQuickLaunchService.s_desiredLaunchCountByTarget, s.countKeys, s.counts);
            Fill(QRAQuickLaunchService.s_pendingSortieByTarget, s.sortieKeys, s.sortieValues);
            Fill(QRAQuickLaunchService.s_launchSortieByInterceptorContact, s.launchKeys, s.launchValues);
            var eta = QRAQuickLaunchService.s_pendingLaunchInfoByTarget;
            eta.Clear();
            for (int i = 0; i < s.etaKeys.Count && i < s.etaValues.Count && i < s.etaTimeline.Count; i++)
            {
                bool timeline = s.etaTimeline[i];
                eta[s.etaKeys[i]] = new QRAQuickLaunchService.PendingLaunchInfo
                {
                    launchAt = timeline ? s.etaValues[i] : Time.unscaledTime + s.etaValues[i],
                    usesTimeline = timeline,
                };
            }
        }

        // ------------------------------------------------------------ supply flights

        [Serializable]
        private sealed class HerculesFlights { public List<HerculesSupplyService.SupplyFlightState> items = new List<HerculesSupplyService.SupplyFlightState>(); }

        [Serializable]
        private sealed class ChinookFlights { public List<ChinookSupplyService.ChinookFlightState> items = new List<ChinookSupplyService.ChinookFlightState>(); }

        public static string CaptureHercules() =>
            JsonUtility.ToJson(new HerculesFlights { items = HerculesSupplyService.ExportSaveState() });

        // Not ImportSaveState: that also purges the flight contacts and re-registers each
        // aircraft as away from its base, which would shift the base inventories every second.
        public static void ApplyHercules(string json)
        {
            var l = JsonUtility.FromJson<HerculesFlights>(json);
            var svc = HerculesSupplyService.Instance;
            if (svc == null) return;
            svc._activeFlights.Clear();
            foreach (var f in l.items) if (f != null) svc._activeFlights.Add(f);
        }

        public static string CaptureChinook() =>
            JsonUtility.ToJson(new ChinookFlights { items = ChinookSupplyService.ExportSaveState() });

        public static void ApplyChinook(string json)
        {
            var l = JsonUtility.FromJson<ChinookFlights>(json);
            var svc = ChinookSupplyService.Instance;
            if (svc == null) return;
            svc._activeFlights.Clear();
            foreach (var f in l.items) if (f != null) svc._activeFlights.Add(f);
        }

        // ------------------------------------------------------------ radio channels

        // Parallel lists of primitives: JsonUtility does not reliably round-trip lists of this
        // assembly's own classes.
        [Serializable]
        private sealed class RadioState
        {
            public List<int> numbers = new List<int>();
            public List<string> keys = new List<string>();
            public List<string> labels = new List<string>();
            public List<string> leads = new List<string>();
            public List<string> wings = new List<string>();
            public List<string> leadIds = new List<string>();
            public List<string> wingIds = new List<string>();
            public List<string> sortieKeys = new List<string>();
            public List<int> sortieNums = new List<int>();
            public List<string> contactKeys = new List<string>();
            public List<int> contactNums = new List<int>();
        }

        private static int _lastLoggedChannels = -1;

        private static void LogChannelCount(string what, int n)
        {
            if (n == _lastLoggedChannels) return;
            _lastLoggedChannels = n;
            CoopLog.Info($"Radio channels {what}: {n}");
        }

        public static string CaptureRadio()
        {
            var s = new RadioState();
            var ui = RadioChannelUI.instance;
            if (ui == null) return JsonUtility.ToJson(s);
            foreach (var c in ui.channels)
            {
                if (c == null) continue;
                s.numbers.Add(c.channelNumber); s.keys.Add(c.sortieKey ?? ""); s.labels.Add(c.displayLabel ?? "");
                s.leads.Add(c.leadCallsign ?? ""); s.wings.Add(c.wingmanCallsign ?? "");
                s.leadIds.Add(c.leadContactId ?? ""); s.wingIds.Add(c.wingContactId ?? "");
            }
            foreach (var kv in ui.channelBySortie) if (kv.Value != null) { s.sortieKeys.Add(kv.Key); s.sortieNums.Add(kv.Value.channelNumber); }
            foreach (var kv in ui.channelByContact) if (kv.Value != null) { s.contactKeys.Add(kv.Key); s.contactNums.Add(kv.Value.channelNumber); }
            LogChannelCount("sent", s.numbers.Count);
            return JsonUtility.ToJson(s);
        }

        private static string NullIfEmpty(List<string> l, int i) => i < l.Count && !string.IsNullOrEmpty(l[i]) ? l[i] : null;

        /// <summary>
        /// Client: make the channel list the host's. Which channel the player has selected stays
        /// their own (RadioChannel.enabled marks the selected one).
        /// </summary>
        public static void ApplyRadio(string json)
        {
            var s = JsonUtility.FromJson<RadioState>(json);
            if (RadioChannelUI.instance == null) RadioChannelUI.EnsureExists();
            var ui = RadioChannelUI.instance;
            if (ui == null) return;

            var byNumber = new Dictionary<int, RadioChannelUI.RadioChannel>();
            foreach (var c in ui.channels) if (c != null && !byNumber.ContainsKey(c.channelNumber)) byNumber[c.channelNumber] = c;
            var ordered = new List<RadioChannelUI.RadioChannel>(s.numbers.Count);
            var keep = new HashSet<RadioChannelUI.RadioChannel>();
            bool added = false;
            for (int i = 0; i < s.numbers.Count; i++)
            {
                int n = s.numbers[i];
                if (!byNumber.TryGetValue(n, out var c))
                {
                    c = new RadioChannelUI.RadioChannel { channelNumber = n, enabled = false, registeredAtUnscaled = Time.unscaledTime };
                    byNumber[n] = c;
                    added = true;
                }
                c.sortieKey = NullIfEmpty(s.keys, i); c.displayLabel = NullIfEmpty(s.labels, i);
                c.leadCallsign = NullIfEmpty(s.leads, i); c.wingmanCallsign = NullIfEmpty(s.wings, i);
                c.leadContactId = NullIfEmpty(s.leadIds, i); c.wingContactId = NullIfEmpty(s.wingIds, i);
                if (keep.Add(c)) ordered.Add(c);
            }
            // RemoveChannel also clears the panel's caches and picks another selection if needed.
            foreach (var c in new List<RadioChannelUI.RadioChannel>(ui.channels))
                if (c != null && !keep.Contains(c)) ui.RemoveChannel(c);

            ui.channels.Clear();
            ui.channels.AddRange(ordered);
            ui.channelBySortie.Clear();
            for (int i = 0; i < s.sortieKeys.Count && i < s.sortieNums.Count; i++)
                if (byNumber.TryGetValue(s.sortieNums[i], out var c) && keep.Contains(c)) ui.channelBySortie[s.sortieKeys[i]] = c;
            ui.channelByContact.Clear();
            for (int i = 0; i < s.contactKeys.Count && i < s.contactNums.Count; i++)
                if (byNumber.TryGetValue(s.contactNums[i], out var c) && keep.Contains(c)) ui.channelByContact[s.contactKeys[i]] = c;

            if (ui.activeChannel != null && !keep.Contains(ui.activeChannel)) ui.activeChannel = null;
            if (ui.activeChannel == null && ordered.Count > 0) ui.ActivateChannel(ordered[0], userInitiated: false);
            foreach (var c in ordered) c.enabled = c == ui.activeChannel;
            if (added) RadioChannelUI.s_panelVisible = true;
            LogChannelCount("applied", ordered.Count);
        }

        // ------------------------------------------------------------ helpers

        private static void Split<TV>(Dictionary<string, TV> d, List<string> keys, List<TV> values)
        {
            foreach (var kv in d) { keys.Add(kv.Key); values.Add(kv.Value); }
        }

        private static void Fill(HashSet<string> set, List<string> items)
        {
            set.Clear();
            foreach (var x in items) if (x != null) set.Add(x);
        }

        private static void Fill<TV>(Dictionary<string, TV> d, List<string> keys, List<TV> values)
        {
            d.Clear();
            for (int i = 0; i < keys.Count && i < values.Count; i++) if (keys[i] != null) d[keys[i]] = values[i];
        }
    }
}
