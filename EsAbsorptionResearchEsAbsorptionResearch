// ES Absorption / Adverse Selection v1.0. Full NinjaTrader 8 indicator source.
// No orders, account access or network calls. Thresholds are research settings.
// CORE_TEST builds the EXACT calculation engine without NinjaTrader assemblies.
using System;
using System.Collections.Generic;
using System.Globalization;
#if !CORE_TEST
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endif

namespace EsAbsorptionResearch
{
    internal sealed class Settings
    {
        public double TickSize = 0.25, WindowSeconds = 10, MinimumVolume = 750;
        public int MinimumPrints = 8, NeighborhoodTicks = 2;
        public double MinimumSeconds = 1, MinimumShare = 0.70, CooldownSeconds = 30;
        public int RejectionTicks = 4, BreachTicks = 2, AdverseTicks = 4;
        public double WatchSeconds = 30, MaximumGapSeconds = 30, MaximumLatenessSeconds = 2;
        public int MaximumSpreadTicks = 4;
        public bool RequireRefill;
        public void Validate()
        {
            foreach (double value in new double[] { TickSize, WindowSeconds, MinimumVolume, MinimumSeconds,
                MinimumShare, CooldownSeconds, WatchSeconds, MaximumGapSeconds, MaximumLatenessSeconds })
                if (!Engine.Finite(value)) throw new ArgumentException("Settings must be finite numbers.");
            if (!Engine.Finite(TickSize) || TickSize <= 0 || WindowSeconds <= 0
                || MinimumVolume <= 0 || MinimumPrints < 2 || MinimumSeconds < 0
                || MinimumSeconds > WindowSeconds || MinimumShare <= 0.5 || MinimumShare > 1
                || NeighborhoodTicks < 0 || CooldownSeconds < WindowSeconds
                || RejectionTicks <= 0 || BreachTicks <= 0 || AdverseTicks <= 0
                || WatchSeconds <= 0 || MaximumGapSeconds <= 0
                || MaximumLatenessSeconds < 0 || MaximumSpreadTicks < 1)
                throw new ArgumentException("Invalid settings. Cooldown must be >= window; minimum duration <= window.");
        }
    }

    internal sealed class Episode
    {
        public int Id, Side, OriginBar, EndBar, Prints, RefillCount, NextHorizon;
        public long PriceKey;
        public DateTime Detected, EndTime;
        public double Level, ReferenceMid, Volume, Share, Duration, RefillVolume;
        public bool Historical;
        public string Resolution = "watching";
    }

    internal sealed class StudyEvent
    {
        public Episode Episode;
        public string Kind, Status;
        public DateTime Time;
        public int Bar, Horizon;
        public double? Midpoint, ResponseTicks, ExecutionMarkoutTicks, ElapsedSeconds;
    }

    internal sealed class Execution
    {
        public DateTime Time;
        public double Volume;
        public int Side;
    }
    internal sealed class QuoteRise
    {
        public DateTime Time;
        public int Side;
        public double Volume;
    }
    internal sealed class PriceWindow
    {
        public readonly Queue<Execution> Trades = new Queue<Execution>();
        public readonly Queue<QuoteRise> Rises = new Queue<QuoteRise>();
        public double Buy, Sell, Unknown;
        public int BuyPrints, SellPrints;
        public DateTime LastBuy = DateTime.MinValue, LastSell = DateTime.MinValue;
        public void Add(Execution trade)
        {
            Trades.Enqueue(trade);
            Change(trade, 1);
            if (trade.Side == 1) LastBuy = trade.Time;
            if (trade.Side == -1) LastSell = trade.Time;
        }
        public void Change(Execution trade, int sign)
        {
            if (trade.Side == 1) { Buy += sign * trade.Volume; BuyPrints += sign; }
            else if (trade.Side == -1) { Sell += sign * trade.Volume; SellPrints += sign; }
            else Unknown += sign * trade.Volume;
        }
        public int Trim(DateTime cutoff)
        {
            int removed = 0;
            while (Trades.Count > 0 && Trades.Peek().Time < cutoff)
            { Change(Trades.Dequeue(), -1); removed++; }
            while (Rises.Count > 0 && Rises.Peek().Time < cutoff) Rises.Dequeue();
            return removed;
        }
    }
    internal sealed class QuoteState
    {
        public DateTime Time = DateTime.MinValue;
        public long Key;
        public double Size;
    }

    internal sealed class Engine
    {
        public readonly Settings Config;
        public Action<StudyEvent> Emit;
        public DateTime LastTrade = DateTime.MinValue;
        public string LastIssue = "warming_up";
        public long TradeEvents, UnknownTrades, BadQuotes;
        public int CandidateCount, ResetCount;
        private readonly Dictionary<long, PriceWindow> windows = new Dictionary<long, PriceWindow>();
        private readonly Dictionary<string, DateTime> cooldowns = new Dictionary<string, DateTime>();
        private readonly List<Episode> active = new List<Episode>();
        private readonly QuoteState ask = new QuoteState(), bid = new QuoteState();
        private readonly int[] horizons = { 5, 15, 60 };
        private DateTime warmupStart = DateTime.MinValue;
        private int nextId, bufferedTrades;
        private const int MaximumBufferedTrades = 100000, MaximumActive = 128;
        public Engine(Settings settings) { settings.Validate(); Config = settings; }
        public static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private long Key(double price) { return (long)Math.Round(price / Config.TickSize); }
        private bool GoodQuote(double bestBid, double bestAsk)
        {
            return Finite(bestBid) && Finite(bestAsk) && bestBid > 0 && bestAsk > bestBid
                && (bestAsk - bestBid) / Config.TickSize <= Config.MaximumSpreadTicks + 0.000001;
        }
        private void Send(Episode episode, string kind, string status, DateTime time, int bar,
            int horizon = 0, double? midpoint = null, double? response = null, double? executionMarkout = null)
        {
            if (Emit != null) Emit(new StudyEvent { Episode = episode, Kind = kind, Status = status,
                Time = time, Bar = bar, Horizon = horizon, Midpoint = midpoint,
                ResponseTicks = response, ExecutionMarkoutTicks = executionMarkout,
                ElapsedSeconds = episode == null ? (double?)null : (time - episode.Detected).TotalSeconds });
        }
        public void Reset(DateTime time, int bar, string reason)
        {
            foreach (Episode episode in active)
            {
                if (episode.Resolution == "watching") Resolve(episode, "censored_" + reason, time, bar);
                while (episode.NextHorizon < horizons.Length)
                    Send(episode, "markout", "censored_" + reason, time, bar, horizons[episode.NextHorizon++]);
            }
            active.Clear(); windows.Clear(); cooldowns.Clear(); bufferedTrades = 0;
            ask.Time = bid.Time = DateTime.MinValue;
            warmupStart = LastTrade = DateTime.MinValue;
            LastIssue = reason; ResetCount++;
            Send(null, "reset", reason, time, bar);
        }
        // This counts observed SAME-PRICE SIZE INCREASES following aggression.
        // It cannot prove iceberg identity, queue position or replenishment causality.
        public void Quote(DateTime time, int bar, int side, double price, double size)
        {
            if (!Finite(price) || price <= 0 || !Finite(size) || size < 0) return;
            QuoteState previous = side == 1 ? ask : bid;
            if (previous.Time != DateTime.MinValue && time < previous.Time)
                Reset(time, bar, "quote_clock_reversal");
            long key = Key(price);
            PriceWindow window;
            if (previous.Time != DateTime.MinValue && previous.Key == key && previous.Size > 0
                && size > previous.Size && (time - previous.Time).TotalSeconds <= Config.WindowSeconds
                && windows.TryGetValue(key, out window))
            {
                DateTime lastAggression = side == 1 ? window.LastBuy : window.LastSell;
                if (lastAggression != DateTime.MinValue && time >= lastAggression
                    && (time - lastAggression).TotalSeconds <= Config.WindowSeconds)
                {
                    while (window.Rises.Count > 0 && (time - window.Rises.Peek().Time).TotalSeconds > Config.WindowSeconds)
                        window.Rises.Dequeue();
                    if (window.Rises.Count < 10000)
                        window.Rises.Enqueue(new QuoteRise { Time = time, Side = side, Volume = size - previous.Size });
                }
            }
            previous.Time = time; previous.Key = key; previous.Size = size;
        }
        public void Trade(DateTime time, int bar, double price, double volume,
            double bestBid, double bestAsk, bool historical)
        {
            if (!Finite(price) || price <= 0 || !Finite(volume) || volume <= 0
                || price / Config.TickSize > long.MaxValue / 2
                || Math.Abs(price - Key(price) * Config.TickSize) > Config.TickSize * 0.000001)
            { Reset(time, bar, "invalid_trade"); return; }
            if (LastTrade != DateTime.MinValue)
            {
                double gap = (time - LastTrade).TotalSeconds;
                if (gap < 0 || gap > Config.MaximumGapSeconds)
                    Reset(time, bar, gap < 0 ? "trade_clock_reversal" : "trade_gap");
            }
            LastTrade = time; TradeEvents++;
            if (warmupStart == DateTime.MinValue) warmupStart = time;
            bool quoteOk = GoodQuote(bestBid, bestAsk);
            double? midpoint = quoteOk ? (double?)((bestBid + bestAsk) / 2) : null;
            Advance(time, bar, price, midpoint);
            if (!quoteOk)
            {
                BadQuotes++; windows.Clear(); bufferedTrades = 0;
                warmupStart = time; LastIssue = "invalid_or_wide_trade_quote";
                return;
            }
            // Inside-spread trades remain unknown; never invent a tick-rule direction.
            int side = price >= bestAsk ? 1 : price <= bestBid ? -1 : 0;
            if (side == 0) UnknownTrades++;
            DateTime cutoff = time.AddSeconds(-Config.WindowSeconds);
            long key = Key(price);
            List<long> remove = new List<long>();
            foreach (KeyValuePair<long, PriceWindow> pair in windows)
            {
                bufferedTrades -= pair.Value.Trim(cutoff);
                // A price excursion starts a fresh local episode. Returning later
                // cannot recycle old volume as though the level held continuously.
                if (Math.Abs(pair.Key - key) > Config.NeighborhoodTicks || pair.Value.Trades.Count == 0)
                { bufferedTrades -= pair.Value.Trades.Count; remove.Add(pair.Key); }
            }
            foreach (long expired in remove) windows.Remove(expired);
            if (bufferedTrades >= MaximumBufferedTrades)
            { Reset(time, bar, "trade_buffer_capacity"); return; }
            PriceWindow level;
            if (!windows.TryGetValue(key, out level))
            { level = new PriceWindow(); windows.Add(key, level); }
            level.Add(new Execution { Time = time, Volume = volume, Side = side }); bufferedTrades++;
            if ((time - warmupStart).TotalSeconds < Config.WindowSeconds)
            { LastIssue = "warming_up"; return; }
            LastIssue = "observing";
            if (side == 0) return;
            double aggressiveVolume = side == 1 ? level.Buy : level.Sell;
            int prints = side == 1 ? level.BuyPrints : level.SellPrints;
            double share = aggressiveVolume / (level.Buy + level.Sell + level.Unknown);
            if (aggressiveVolume < Config.MinimumVolume || prints < Config.MinimumPrints || share < Config.MinimumShare) return;
            // Avoid rescanning a busy level's evidence on every tick during cooldown.
            string cooldownKey = key.ToString(CultureInfo.InvariantCulture) + ":" + side;
            DateTime previous;
            if (cooldowns.TryGetValue(cooldownKey, out previous)
                && (time - previous).TotalSeconds < Config.CooldownSeconds) return;
            foreach (Episode old in active)
                if (old.PriceKey == key && old.Side == side && old.Resolution == "watching") return;
            DateTime first = time;
            foreach (Execution trade in level.Trades)
                if (trade.Side == side) { first = trade.Time; break; }
            double duration = (time - first).TotalSeconds;
            if (duration < Config.MinimumSeconds) return;
            int refillCount = 0; double refillVolume = 0;
            foreach (QuoteRise rise in level.Rises)
                if (rise.Side == side && rise.Time >= first && rise.Time <= time)
                { refillCount++; refillVolume += rise.Volume; }
            if (Config.RequireRefill && refillCount == 0) return;
            if (active.Count >= MaximumActive)
            { LastIssue = "active_capacity_no_new_candidate"; return; }
            List<string> expiredCooldowns = new List<string>();
            foreach (KeyValuePair<string, DateTime> pair in cooldowns)
                if ((time - pair.Value).TotalSeconds >= Config.CooldownSeconds) expiredCooldowns.Add(pair.Key);
            foreach (string expired in expiredCooldowns) cooldowns.Remove(expired);
            cooldowns[cooldownKey] = time;
            Episode episode = new Episode { Id = ++nextId, Side = side, PriceKey = key,
                Level = key * Config.TickSize, ReferenceMid = midpoint.Value, OriginBar = bar, EndBar = bar,
                Detected = time, Historical = historical, Volume = aggressiveVolume, Share = share,
                Prints = prints, Duration = duration, RefillCount = refillCount, RefillVolume = refillVolume };
            active.Add(episode); CandidateCount++;
            Send(episode, "candidate", "absorption_candidate", time, bar, 0, midpoint);
        }
        private void Resolve(Episode episode, string status, DateTime time, int bar)
        {
            episode.Resolution = status; episode.EndBar = bar; episode.EndTime = time;
            Send(episode, "resolution", status, time, bar);
        }
        private void Advance(DateTime time, int bar, double price, double? midpoint)
        {
            for (int index = active.Count - 1; index >= 0; index--)
            {
                Episode episode = active[index];
                double elapsed = (time - episode.Detected).TotalSeconds;
                if (episode.Resolution == "watching")
                {
                    double progress = episode.Side * (price - episode.Level) / Config.TickSize;
                    if (elapsed <= Config.WatchSeconds && progress >= Config.BreachTicks)
                        Resolve(episode, "breach_first", time, bar);
                    else if (elapsed <= Config.WatchSeconds && progress <= -Config.RejectionTicks)
                        Resolve(episode, "rejection_first", time, bar);
                    else if (elapsed >= Config.WatchSeconds) Resolve(episode, "expired_no_barrier", time, bar);
                }
                while (episode.NextHorizon < horizons.Length && elapsed >= horizons[episode.NextHorizon])
                {
                    int horizon = horizons[episode.NextHorizon++];
                    if (elapsed - horizon > Config.MaximumLatenessSeconds)
                        Send(episode, "markout", "late_observation", time, bar, horizon);
                    else if (!midpoint.HasValue)
                        Send(episode, "markout", "invalid_or_wide_quote", time, bar, horizon);
                    else
                    {
                        double response = episode.Side * (midpoint.Value - episode.ReferenceMid) / Config.TickSize;
                        double executionMarkout = episode.Side * (midpoint.Value - episode.Level) / Config.TickSize;
                        Send(episode, "markout", response >= Config.AdverseTicks ? "adverse_move" : "measured",
                            time, bar, horizon, midpoint, response, executionMarkout);
                    }
                }
                if (episode.NextHorizon == horizons.Length && episode.Resolution != "watching") active.RemoveAt(index);
            }
        }
    }
}
#if !CORE_TEST
namespace NinjaTrader.NinjaScript.Indicators
{
    public class EsAbsorptionToxicity : Indicator
    {
        private readonly object gate = new object();
        private EsAbsorptionResearch.Engine engine;
        private readonly List<EsAbsorptionResearch.Episode> visible = new List<EsAbsorptionResearch.Episode>();
        private readonly Dictionary<int, List<string>> objectTags = new Dictionary<int, List<string>>();
        private StreamWriter eventWriter, rawWriter;
        private string runId, folder, stopReason = "", notice = "Waiting for trades";
        private int lastPrimaryBar = -1;
        private bool stopped, terminating, replayWasProcessed;
        private DateTime lastPanelTime = DateTime.MinValue, lastRawFlush = DateTime.MinValue;
        private readonly SimpleFont labelFont = new SimpleFont("Arial", 11);

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "EsAbsorptionToxicity";
                Description = "Price-level absorption candidates and delayed adverse-selection measurements on ES delta bars.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DrawOnPricePanel = true;
                IsSuspendedWhileInactive = false;
                BarsRequiredToPlot = 0;
                WindowSeconds = 10; MinimumVolumeAtPrice = 750; MinimumPrints = 8;
                MinimumDurationSeconds = 1; MinimumAggressorShare = 0.70; NeighborhoodTicks = 2;
                RequireRefillEvidence = false; CooldownSeconds = 30;
                RejectionTicks = 4; BreachTicks = 2; WatchSeconds = 30; AdverseMoveTicks = 4;
                MaximumTickGapSeconds = 30; MaximumMarkoutLatenessSeconds = 2; MaximumSpreadTicks = 4;
                EnableHistoricalReplay = true; HistoricalBidAskVerified = false; LiveSourceVerified = false;
                MarkerLookbackBars = 500; MaximumVisibleEvents = 80; ShowOutcomeLabels = true; ShowStatusPanel = true;
                SaveEventLog = true; SaveRawTape = false;
                StudyLabel = "ES +/-500 delta; set feed and Live or Market Replay";
            }
            else if (State == State.DataLoaded)
            {
                try
                {
                    if (Calculate != Calculate.OnEachTick)
                        throw new InvalidOperationException("Set Calculate to On each tick.");
                    EsAbsorptionResearch.Settings settings = new EsAbsorptionResearch.Settings
                    {
                        TickSize = TickSize, WindowSeconds = WindowSeconds, MinimumVolume = MinimumVolumeAtPrice,
                        MinimumPrints = MinimumPrints, MinimumSeconds = MinimumDurationSeconds,
                        MinimumShare = MinimumAggressorShare, NeighborhoodTicks = NeighborhoodTicks,
                        RequireRefill = RequireRefillEvidence, CooldownSeconds = CooldownSeconds,
                        RejectionTicks = RejectionTicks, BreachTicks = BreachTicks, WatchSeconds = WatchSeconds,
                        AdverseTicks = AdverseMoveTicks, MaximumGapSeconds = MaximumTickGapSeconds,
                        MaximumLatenessSeconds = MaximumMarkoutLatenessSeconds, MaximumSpreadTicks = MaximumSpreadTicks
                    };
                    engine = new EsAbsorptionResearch.Engine(settings);
                    runId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
                        + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    OpenLogs();
                    engine.Emit = HandleEvent;
                }
                catch (Exception exception) { Stop(exception); }
            }
            else if (State == State.Realtime)
            {
                lock (gate)
                {
                    if (stopped || engine == null) return;
                    try
                    {
                        if (replayWasProcessed)
                            engine.Reset(engine.LastTrade, Math.Max(0, lastPrimaryBar), "historical_live_boundary");
                        notice = "Observing live/Market Replay ticks; warming up for " + WindowSeconds + " seconds";
                        Panel();
                    }
                    catch (Exception exception) { Stop(exception); }
                }
            }
            else if (State == State.Terminated)
            {
                lock (gate)
                {
                    terminating = true;
                    try
                    {
                        if (!stopped && engine != null && engine.LastTrade != DateTime.MinValue)
                            engine.Reset(engine.LastTrade, Math.Max(0, lastPrimaryBar), "indicator_stopped");
                    }
                    catch (Exception exception) { Print(Name + ": " + exception.Message); }
                    finally { CloseLogs(); }
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            lock (gate)
            {
                if (stopped || engine == null) return;
                try
                {
                    if (CurrentBar < lastPrimaryBar)
                        throw new InvalidOperationException("Bars moved backwards. Reload after replay seek or rebuilt bars.");
                    bool newBar = CurrentBar != lastPrimaryBar;
                    lastPrimaryBar = CurrentBar;
                    if (newBar)
                    {
                        PruneVisible();
                        foreach (EsAbsorptionResearch.Episode episode in visible)
                            if (episode.Resolution == "watching") DrawZone(episode, CurrentBar);
                    }
                    if (State == State.Historical && CurrentBar >= Bars.Count - 1)
                    {
                        notice = !EnableHistoricalReplay ? "Historical reconstruction switched off"
                            : !HistoricalBidAskVerified ? "History requires checked bid/ask data and Tick Replay"
                            : !Bars.IsTickReplay ? "Enable Tick Replay on this chart for historical candidates"
                            : RequireRefillEvidence ? "Historical refill evidence unavailable; candidates filtered out"
                            : "Reconstructing trade-based history; quote-size refills unavailable";
                        Panel();
                    }
                }
                catch (Exception exception) { Stop(exception); }
            }
        }

        protected override void OnMarketData(MarketDataEventArgs update)
        {
            if (update.Instrument == null || Instrument == null
                || update.Instrument.FullName != Instrument.FullName) return;
            lock (gate)
            {
                if (stopped || terminating || engine == null || lastPrimaryBar < 0) return;
                bool historical = State == State.Historical;
                if (historical && (!EnableHistoricalReplay || !HistoricalBidAskVerified || !Bars.IsTickReplay)) return;
                if (!historical && State != State.Realtime) return;
                try
                {
                    if (historical) replayWasProcessed = true;
                    int bar = lastPrimaryBar;
                    if (update.IsReset)
                    { engine.Reset(update.Time, bar, "market_data_reset"); return; }
                    if (update.MarketDataType == MarketDataType.Last)
                    {
                        Tape("trade", update, bar, historical);
                        engine.Trade(update.Time, bar, update.Price, update.Volume, update.Bid, update.Ask, historical);
                        if (lastPanelTime == DateTime.MinValue || (update.Time - lastPanelTime).TotalSeconds >= 1)
                        { lastPanelTime = update.Time; Panel(); }
                    }
                    else if (!historical && (update.MarketDataType == MarketDataType.Bid
                        || update.MarketDataType == MarketDataType.Ask))
                    {
                        int side = update.MarketDataType == MarketDataType.Ask ? 1 : -1;
                        Tape(side == 1 ? "ask_size" : "bid_size", update, bar, false);
                        engine.Quote(update.Time, bar, side, update.Price, update.Volume);
                    }
                }
                catch (Exception exception) { Stop(exception); }
            }
        }

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs update)
        {
            if (update.PriceStatus == ConnectionStatus.Connected) return;
            lock (gate)
            {
                if (stopped || terminating || engine == null || engine.LastTrade == DateTime.MinValue) return;
                try
                {
                    engine.Reset(engine.LastTrade, Math.Max(0, lastPrimaryBar), "price_connection_lost");
                    notice = "Connection interrupted; unfinished observations censored";
                    Panel();
                }
                catch (Exception exception) { Stop(exception); }
            }
        }

        private void HandleEvent(EsAbsorptionResearch.StudyEvent item)
        {
            EsAbsorptionResearch.Episode episode = item.Episode;
            if (eventWriter != null)
                Csv(eventWriter, "1", runId, episode == null ? null : (object)episode.Id,
                    item.Kind, item.Status, Stamp(item.Time), item.Bar,
                    episode == null ? null : Stamp(episode.Detected), episode == null ? null : (object)episode.OriginBar,
                    episode == null ? null : episode.Historical ? "historical_tick_replay" : "live_or_market_replay",
                    episode == null ? null : (object)(episode.Historical ? HistoricalBidAskVerified : LiveSourceVerified),
                    episode == null ? null : episode.Side == 1 ? "passive_sellers" : "passive_buyers",
                    episode == null ? null : (object)episode.Level, WindowSeconds,
                    episode == null ? null : (object)episode.Volume, episode == null ? null : (object)episode.Prints,
                    episode == null ? null : (object)episode.Share, episode == null ? null : (object)episode.Duration,
                    episode == null ? null : (object)episode.RefillCount, episode == null ? null : (object)episode.RefillVolume,
                    episode == null ? null : (object)episode.ReferenceMid,
                    item.Horizon == 0 ? null : (object)item.Horizon, item.ElapsedSeconds,
                    item.Midpoint, item.ResponseTicks, item.ExecutionMarkoutTicks);
            if (episode == null || terminating) return;
            if (item.Kind == "candidate")
            {
                notice = Absorber(episode) + " candidate at " + Number(episode.Level)
                    + ": " + Number(episode.Volume) + " contracts / " + Number(episode.Duration) + "s";
                // Compute every tick for warm-up, but draw only the requested history.
                if (episode.Historical && episode.OriginBar < Bars.Count - MarkerLookbackBars) return;
                visible.Add(episode); objectTags[episode.Id] = new List<string>();
                PruneVisible();
                if (!objectTags.ContainsKey(episode.Id)) return;
                string tag = Tag(episode, "origin");
                double offset = episode.Side * 3 * TickSize;
                Draw.Diamond(this, tag, true, Math.Max(0, CurrentBar - episode.OriginBar), episode.Level, CandidateBrush(episode));
                string label = (episode.Historical ? "H " : "") + "#" + episode.Id + " "
                    + (episode.Side == 1 ? "SA?" : "BA?") + (episode.RefillCount > 0 ? " R" : "")
                    + "\n" + Number(episode.Volume) + " @ " + Number(episode.Level)
                    + " / " + Number(episode.Duration) + "s";
                TextAt(episode, "origin_text", label, episode.OriginBar, episode.Level + offset,
                    CandidateBrush(episode), episode.Side * 15);
                DrawZone(episode, CurrentBar);
            }
            else if (objectTags.ContainsKey(episode.Id))
            {
                if (item.Kind == "resolution")
                {
                    DrawZone(episode, item.Bar);
                    if (ShowOutcomeLabels)
                    {
                        string label = item.Status == "rejection_first" ? "REJECT first"
                            : item.Status == "breach_first" ? "BREACH first"
                            : item.Status == "expired_no_barrier" ? "EXPIRED" : "CENSORED";
                        TextAt(episode, "resolution", "#" + episode.Id + " " + label + "\n+"
                            + Number(item.ElapsedSeconds.Value) + "s", item.Bar,
                            episode.Level + episode.Side * 6 * TickSize, Brushes.SlateGray, episode.Side * 20);
                    }
                }
                if (item.Kind == "markout" && ShowOutcomeLabels)
                {
                    string measured = item.ResponseTicks.HasValue ? Signed(item.ResponseTicks.Value) + "t" : "n/a";
                    string label = "#" + episode.Id + " AS" + item.Horizon + " " + measured;
                    if (item.Status == "adverse_move") label += " ADV";
                    if (!item.ResponseTicks.HasValue) label += "\n" + item.Status.Replace('_', ' ');
                    Brush brush = item.Status == "adverse_move" ? Brushes.MediumVioletRed : Brushes.SlateGray;
                    // Outcome is drawn where/when it becomes known, never on the origin bar.
                    double anchor = item.Midpoint.HasValue ? item.Midpoint.Value : episode.Level;
                    int tier = item.Horizon == 5 ? 0 : item.Horizon == 15 ? 1 : 2;
                    TextAt(episode, "as" + item.Horizon, label, item.Bar,
                        anchor + episode.Side * (9 + tier * 4) * TickSize, brush, episode.Side * 15);
                }
            }
        }

        private string Tag(EsAbsorptionResearch.Episode episode, string suffix)
        {
            string tag = "EAT_" + runId + "_" + episode.Id + "_" + suffix;
            List<string> tags;
            if (objectTags.TryGetValue(episode.Id, out tags) && !tags.Contains(tag)) tags.Add(tag);
            return tag;
        }
        private Brush CandidateBrush(EsAbsorptionResearch.Episode episode)
        {
            Brush brush = episode.Side == 1 ? Brushes.DarkOrange : Brushes.DodgerBlue;
            if (!episode.Historical) return brush;
            brush = brush.Clone(); brush.Opacity = 0.6; brush.Freeze(); return brush;
        }
        private void DrawZone(EsAbsorptionResearch.Episode episode, int currentBar)
        {
            if (ChartControl == null || !objectTags.ContainsKey(episode.Id)) return;
            int end = episode.Resolution == "watching" ? currentBar : episode.EndBar;
            Draw.Rectangle(this, Tag(episode, "zone"), false,
                Math.Max(0, CurrentBar - episode.OriginBar), episode.Level + TickSize / 2,
                Math.Max(0, CurrentBar - end), episode.Level - TickSize / 2,
                CandidateBrush(episode), CandidateBrush(episode), 12);
        }
        private void TextAt(EsAbsorptionResearch.Episode episode, string suffix, string text,
            int bar, double price, Brush brush, int pixelOffset)
        {
            if (ChartControl == null) return;
            Draw.Text(this, Tag(episode, suffix), true, text, Math.Max(0, CurrentBar - bar), price,
                pixelOffset, brush, labelFont, System.Windows.TextAlignment.Center,
                Brushes.Transparent, Brushes.Transparent, 0);
        }
        private void PruneVisible()
        {
            while (visible.Count > 0 && (visible.Count > MaximumVisibleEvents
                || visible[0].OriginBar < CurrentBar - MarkerLookbackBars + 1))
            {
                EsAbsorptionResearch.Episode oldest = visible[0];
                List<string> tags;
                if (objectTags.TryGetValue(oldest.Id, out tags))
                    foreach (string tag in tags) RemoveDrawObject(tag);
                objectTags.Remove(oldest.Id); visible.RemoveAt(0);
            }
        }
        private static string Absorber(EsAbsorptionResearch.Episode episode)
        { return episode.Side == 1 ? "Sell absorption" : "Buy absorption"; }
        private static string Number(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }
        private static string Signed(double value) { return value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture); }
        private static string Stamp(DateTime time) { return time.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture); }
        private void Panel()
        {
            if (!ShowStatusPanel || ChartControl == null) return;
            string history = !EnableHistoricalReplay ? "History off"
                : !HistoricalBidAskVerified ? "History gated: verify genuine historical bid/ask, then enable its checkbox"
                : !Bars.IsTickReplay ? "History needs Tick Replay (bar type/feed must support it)"
                : "H = reconstructed ticks; historical size-refill evidence unavailable";
            string text = "ES ABSORPTION / ADVERSE SELECTION | " + (stopped ? "STOPPED" : "research observations")
                + "\nOrange SA? = sellers absorbing buys | Blue BA? = buyers absorbing sells | R = size-rise evidence"
                + "\nAS5/15/60 = later midpoint response; positive = adverse to passive side; ADV >= " + AdverseMoveTicks + " ticks"
                + "\n" + (LiveSourceVerified ? "Live source marked checked by user" : "LIVE SOURCE UNVERIFIED")
                + " | " + history
                + "\n" + (stopped ? stopReason : notice)
                + (engine == null ? "" : "\nEngine: " + engine.LastIssue + " | Candidates " + engine.CandidateCount
                    + " | Unknown trades " + engine.UnknownTrades + " | Bad quotes " + engine.BadQuotes);
            Draw.TextFixed(this, "EAT_Status", text, TextPosition.TopLeft);
        }
        private void OpenLogs()
        {
            if (!SaveEventLog && !SaveRawTape) return;
            folder = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "AbsorptionToxicityLogs");
            Directory.CreateDirectory(folder);
            if (SaveEventLog)
            {
                eventWriter = Writer(Path.Combine(folder, runId + "_events.csv"), true);
                eventWriter.WriteLine("schema,run_id,event_id,event_type,status,event_time,primary_bar,detected_time,origin_bar,mode,source_verified,absorber,level,window_seconds,executed_volume,prints,side_share,span_seconds,refill_count,refill_added_volume,reference_midpoint,horizon_seconds,elapsed_seconds,observed_midpoint,adverse_midpoint_ticks,adverse_execution_markout_ticks");
            }
            if (SaveRawTape)
            {
                rawWriter = Writer(Path.Combine(folder, runId + "_tape.csv"), false);
                rawWriter.WriteLine("event_type,event_time,primary_bar,price,volume,bid,ask,mode");
            }
            using (StreamWriter settings = Writer(Path.Combine(folder, runId + "_settings.txt"), true))
            {
                settings.WriteLine("EsAbsorptionToxicity v1.0\nRun=" + runId + "\nInstrument=" + Instrument.FullName);
                settings.WriteLine("Bars=" + BarsPeriod + "\nTradingHours=" + Bars.TradingHours.Name);
                settings.WriteLine("PlatformTimeZone=" + NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo.Id);
                settings.WriteLine("StudyLabel=" + StudyLabel + "\nLiveSourceVerified=" + LiveSourceVerified
                    + "\nHistoricalBidAskVerified=" + HistoricalBidAskVerified + "\nTickReplay=" + Bars.IsTickReplay);
                foreach (System.Reflection.FieldInfo field in typeof(EsAbsorptionResearch.Settings).GetFields())
                    settings.WriteLine(field.Name + "=" + Convert.ToString(field.GetValue(engine.Config), CultureInfo.InvariantCulture));
            }
            Print(Name + " log folder: " + folder + " | run " + runId);
        }
        private void Tape(string kind, MarketDataEventArgs update, int bar, bool historical)
        {
            if (rawWriter == null) return;
            Csv(rawWriter, kind, Stamp(update.Time), bar, update.Price, update.Volume,
                update.Bid, update.Ask, historical ? "historical_tick_replay" : "live_or_market_replay");
            if (lastRawFlush == DateTime.MinValue || (update.Time - lastRawFlush).TotalSeconds >= 1)
            { rawWriter.Flush(); lastRawFlush = update.Time; }
        }
        private static StreamWriter Writer(string path, bool autoFlush)
        {
            StreamWriter writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true));
            writer.AutoFlush = autoFlush; return writer;
        }
        private static void Csv(StreamWriter writer, params object[] values)
        {
            string[] cells = new string[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                string text = values[index] == null ? "" : Convert.ToString(values[index], CultureInfo.InvariantCulture);
                cells[index] = "\"" + text.Replace("\"", "\"\"") + "\"";
            }
            writer.WriteLine(string.Join(",", cells));
        }
        private void Stop(Exception exception)
        {
            stopped = true; stopReason = exception.Message;
            Print(Name + " STOPPED: " + stopReason + ". Incomplete horizons are not results. Correct and reload.");
            CloseLogs();
            try { Panel(); } catch { }
        }
        private void CloseLogs()
        {
            try { if (eventWriter != null) eventWriter.Dispose(); } catch { }
            try { if (rawWriter != null) rawWriter.Dispose(); } catch { }
            eventWriter = null; rawWriter = null;
        }

        [NinjaScriptProperty, Range(1.0, 120.0)]
        [Display(Name = "Window (seconds)", GroupName = "1. Absorption", Order = 1)]
        public double WindowSeconds { get; set; }
        [NinjaScriptProperty, Range(1.0, 10000000.0)]
        [Display(Name = "Minimum aggressive contracts AT ONE PRICE", GroupName = "1. Absorption", Order = 2)]
        public double MinimumVolumeAtPrice { get; set; }
        [NinjaScriptProperty, Range(2, 10000)]
        [Display(Name = "Minimum aggressive trade events", GroupName = "1. Absorption", Order = 3)]
        public int MinimumPrints { get; set; }
        [NinjaScriptProperty, Range(0.0, 120.0)]
        [Display(Name = "Minimum duration (seconds)", GroupName = "1. Absorption", Order = 4)]
        public double MinimumDurationSeconds { get; set; }
        [NinjaScriptProperty, Range(0.51, 1.0)]
        [Display(Name = "Minimum aggressive side share", GroupName = "1. Absorption", Order = 5)]
        public double MinimumAggressorShare { get; set; }
        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Maximum price excursion from level (ticks)", GroupName = "1. Absorption", Order = 6)]
        public int NeighborhoodTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Require observed same-price size increase", GroupName = "1. Absorption", Order = 7)]
        public bool RequireRefillEvidence { get; set; }
        [NinjaScriptProperty, Range(1.0, 600.0)]
        [Display(Name = "Same-level cooldown (seconds; >= window)", GroupName = "1. Absorption", Order = 8)]
        public double CooldownSeconds { get; set; }
        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Rejection distance (ticks)", GroupName = "2. Later observations", Order = 1)]
        public int RejectionTicks { get; set; }
        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Breach distance (ticks)", GroupName = "2. Later observations", Order = 2)]
        public int BreachTicks { get; set; }
        [NinjaScriptProperty, Range(1.0, 300.0)]
        [Display(Name = "Barrier observation window (seconds)", GroupName = "2. Later observations", Order = 3)]
        public double WatchSeconds { get; set; }
        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Adverse midpoint move flag (ticks)", GroupName = "2. Later observations", Order = 4)]
        public int AdverseMoveTicks { get; set; }
        [NinjaScriptProperty, Range(1.0, 300.0)]
        [Display(Name = "Maximum gap between trades (seconds)", GroupName = "3. Data", Order = 1)]
        public double MaximumTickGapSeconds { get; set; }
        [NinjaScriptProperty, Range(0.0, 10.0)]
        [Display(Name = "Maximum markout lateness (seconds)", GroupName = "3. Data", Order = 2)]
        public double MaximumMarkoutLatenessSeconds { get; set; }
        [NinjaScriptProperty, Range(1, 40)]
        [Display(Name = "Maximum spread (ticks)", GroupName = "3. Data", Order = 3)]
        public int MaximumSpreadTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Enable historical Tick Replay reconstruction", GroupName = "3. Data", Order = 4)]
        public bool EnableHistoricalReplay { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "I verified GENUINE historical bid/ask ticks", GroupName = "3. Data", Order = 5)]
        public bool HistoricalBidAskVerified { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "I checked live/replay trades and quotes", GroupName = "3. Data", Order = 6)]
        public bool LiveSourceVerified { get; set; }
        [NinjaScriptProperty, Range(20, 10000)]
        [Display(Name = "Marker lookback (primary bars)", GroupName = "4. Display", Order = 1)]
        public int MarkerLookbackBars { get; set; }
        [NinjaScriptProperty, Range(1, 300)]
        [Display(Name = "Maximum visible events", GroupName = "4. Display", Order = 2)]
        public int MaximumVisibleEvents { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show later resolution and AS labels", GroupName = "4. Display", Order = 3)]
        public bool ShowOutcomeLabels { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show status and legend", GroupName = "4. Display", Order = 4)]
        public bool ShowStatusPanel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Save event CSV", GroupName = "5. Recording", Order = 1)]
        public bool SaveEventLog { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Save raw trade/quote tape (larger files)", GroupName = "5. Recording", Order = 2)]
        public bool SaveRawTape { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Study label (feed, contract, Live/Market Replay)", GroupName = "5. Recording", Order = 3)]
        public string StudyLabel { get; set; }
    }
}
#endif


#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private EsAbsorptionToxicity[] cacheEsAbsorptionToxicity;
		public EsAbsorptionToxicity EsAbsorptionToxicity(double windowSeconds, double minimumVolumeAtPrice, int minimumPrints, double minimumDurationSeconds, double minimumAggressorShare, int neighborhoodTicks, bool requireRefillEvidence, double cooldownSeconds, int rejectionTicks, int breachTicks, double watchSeconds, int adverseMoveTicks, double maximumTickGapSeconds, double maximumMarkoutLatenessSeconds, int maximumSpreadTicks, bool enableHistoricalReplay, bool historicalBidAskVerified, bool liveSourceVerified, int markerLookbackBars, int maximumVisibleEvents, bool showOutcomeLabels, bool showStatusPanel, bool saveEventLog, bool saveRawTape, string studyLabel)
		{
			return EsAbsorptionToxicity(Input, windowSeconds, minimumVolumeAtPrice, minimumPrints, minimumDurationSeconds, minimumAggressorShare, neighborhoodTicks, requireRefillEvidence, cooldownSeconds, rejectionTicks, breachTicks, watchSeconds, adverseMoveTicks, maximumTickGapSeconds, maximumMarkoutLatenessSeconds, maximumSpreadTicks, enableHistoricalReplay, historicalBidAskVerified, liveSourceVerified, markerLookbackBars, maximumVisibleEvents, showOutcomeLabels, showStatusPanel, saveEventLog, saveRawTape, studyLabel);
		}

		public EsAbsorptionToxicity EsAbsorptionToxicity(ISeries<double> input, double windowSeconds, double minimumVolumeAtPrice, int minimumPrints, double minimumDurationSeconds, double minimumAggressorShare, int neighborhoodTicks, bool requireRefillEvidence, double cooldownSeconds, int rejectionTicks, int breachTicks, double watchSeconds, int adverseMoveTicks, double maximumTickGapSeconds, double maximumMarkoutLatenessSeconds, int maximumSpreadTicks, bool enableHistoricalReplay, bool historicalBidAskVerified, bool liveSourceVerified, int markerLookbackBars, int maximumVisibleEvents, bool showOutcomeLabels, bool showStatusPanel, bool saveEventLog, bool saveRawTape, string studyLabel)
		{
			if (cacheEsAbsorptionToxicity != null)
				for (int idx = 0; idx < cacheEsAbsorptionToxicity.Length; idx++)
					if (cacheEsAbsorptionToxicity[idx] != null && cacheEsAbsorptionToxicity[idx].WindowSeconds == windowSeconds && cacheEsAbsorptionToxicity[idx].MinimumVolumeAtPrice == minimumVolumeAtPrice && cacheEsAbsorptionToxicity[idx].MinimumPrints == minimumPrints && cacheEsAbsorptionToxicity[idx].MinimumDurationSeconds == minimumDurationSeconds && cacheEsAbsorptionToxicity[idx].MinimumAggressorShare == minimumAggressorShare && cacheEsAbsorptionToxicity[idx].NeighborhoodTicks == neighborhoodTicks && cacheEsAbsorptionToxicity[idx].RequireRefillEvidence == requireRefillEvidence && cacheEsAbsorptionToxicity[idx].CooldownSeconds == cooldownSeconds && cacheEsAbsorptionToxicity[idx].RejectionTicks == rejectionTicks && cacheEsAbsorptionToxicity[idx].BreachTicks == breachTicks && cacheEsAbsorptionToxicity[idx].WatchSeconds == watchSeconds && cacheEsAbsorptionToxicity[idx].AdverseMoveTicks == adverseMoveTicks && cacheEsAbsorptionToxicity[idx].MaximumTickGapSeconds == maximumTickGapSeconds && cacheEsAbsorptionToxicity[idx].MaximumMarkoutLatenessSeconds == maximumMarkoutLatenessSeconds && cacheEsAbsorptionToxicity[idx].MaximumSpreadTicks == maximumSpreadTicks && cacheEsAbsorptionToxicity[idx].EnableHistoricalReplay == enableHistoricalReplay && cacheEsAbsorptionToxicity[idx].HistoricalBidAskVerified == historicalBidAskVerified && cacheEsAbsorptionToxicity[idx].LiveSourceVerified == liveSourceVerified && cacheEsAbsorptionToxicity[idx].MarkerLookbackBars == markerLookbackBars && cacheEsAbsorptionToxicity[idx].MaximumVisibleEvents == maximumVisibleEvents && cacheEsAbsorptionToxicity[idx].ShowOutcomeLabels == showOutcomeLabels && cacheEsAbsorptionToxicity[idx].ShowStatusPanel == showStatusPanel && cacheEsAbsorptionToxicity[idx].SaveEventLog == saveEventLog && cacheEsAbsorptionToxicity[idx].SaveRawTape == saveRawTape && cacheEsAbsorptionToxicity[idx].StudyLabel == studyLabel && cacheEsAbsorptionToxicity[idx].EqualsInput(input))
						return cacheEsAbsorptionToxicity[idx];
			return CacheIndicator<EsAbsorptionToxicity>(new EsAbsorptionToxicity(){ WindowSeconds = windowSeconds, MinimumVolumeAtPrice = minimumVolumeAtPrice, MinimumPrints = minimumPrints, MinimumDurationSeconds = minimumDurationSeconds, MinimumAggressorShare = minimumAggressorShare, NeighborhoodTicks = neighborhoodTicks, RequireRefillEvidence = requireRefillEvidence, CooldownSeconds = cooldownSeconds, RejectionTicks = rejectionTicks, BreachTicks = breachTicks, WatchSeconds = watchSeconds, AdverseMoveTicks = adverseMoveTicks, MaximumTickGapSeconds = maximumTickGapSeconds, MaximumMarkoutLatenessSeconds = maximumMarkoutLatenessSeconds, MaximumSpreadTicks = maximumSpreadTicks, EnableHistoricalReplay = enableHistoricalReplay, HistoricalBidAskVerified = historicalBidAskVerified, LiveSourceVerified = liveSourceVerified, MarkerLookbackBars = markerLookbackBars, MaximumVisibleEvents = maximumVisibleEvents, ShowOutcomeLabels = showOutcomeLabels, ShowStatusPanel = showStatusPanel, SaveEventLog = saveEventLog, SaveRawTape = saveRawTape, StudyLabel = studyLabel }, input, ref cacheEsAbsorptionToxicity);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.EsAbsorptionToxicity EsAbsorptionToxicity(double windowSeconds, double minimumVolumeAtPrice, int minimumPrints, double minimumDurationSeconds, double minimumAggressorShare, int neighborhoodTicks, bool requireRefillEvidence, double cooldownSeconds, int rejectionTicks, int breachTicks, double watchSeconds, int adverseMoveTicks, double maximumTickGapSeconds, double maximumMarkoutLatenessSeconds, int maximumSpreadTicks, bool enableHistoricalReplay, bool historicalBidAskVerified, bool liveSourceVerified, int markerLookbackBars, int maximumVisibleEvents, bool showOutcomeLabels, bool showStatusPanel, bool saveEventLog, bool saveRawTape, string studyLabel)
		{
			return indicator.EsAbsorptionToxicity(Input, windowSeconds, minimumVolumeAtPrice, minimumPrints, minimumDurationSeconds, minimumAggressorShare, neighborhoodTicks, requireRefillEvidence, cooldownSeconds, rejectionTicks, breachTicks, watchSeconds, adverseMoveTicks, maximumTickGapSeconds, maximumMarkoutLatenessSeconds, maximumSpreadTicks, enableHistoricalReplay, historicalBidAskVerified, liveSourceVerified, markerLookbackBars, maximumVisibleEvents, showOutcomeLabels, showStatusPanel, saveEventLog, saveRawTape, studyLabel);
		}

		public Indicators.EsAbsorptionToxicity EsAbsorptionToxicity(ISeries<double> input , double windowSeconds, double minimumVolumeAtPrice, int minimumPrints, double minimumDurationSeconds, double minimumAggressorShare, int neighborhoodTicks, bool requireRefillEvidence, double cooldownSeconds, int rejectionTicks, int breachTicks, double watchSeconds, int adverseMoveTicks, double maximumTickGapSeconds, double maximumMarkoutLatenessSeconds, int maximumSpreadTicks, bool enableHistoricalReplay, bool historicalBidAskVerified, bool liveSourceVerified, int markerLookbackBars, int maximumVisibleEvents, bool showOutcomeLabels, bool showStatusPanel, bool saveEventLog, bool saveRawTape, string studyLabel)
		{
			return indicator.EsAbsorptionToxicity(input, windowSeconds, minimumVolumeAtPrice, minimumPrints, minimumDurationSeconds, minimumAggressorShare, neighborhoodTicks, requireRefillEvidence, cooldownSeconds, rejectionTicks, breachTicks, watchSeconds, adverseMoveTicks, maximumTickGapSeconds, maximumMarkoutLatenessSeconds, maximumSpreadTicks, enableHistoricalReplay, historicalBidAskVerified, liveSourceVerified, markerLookbackBars, maximumVisibleEvents, showOutcomeLabels, showStatusPanel, saveEventLog, saveRawTape, studyLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.EsAbsorptionToxicity EsAbsorptionToxicity(double windowSeconds, double minimumVolumeAtPrice, int minimumPrints, double minimumDurationSeconds, double minimumAggressorShare, int neighborhoodTicks, bool requireRefillEvidence, double cooldownSeconds, int rejectionTicks, int breachTicks, double watchSeconds, int adverseMoveTicks, double maximumTickGapSeconds, double maximumMarkoutLatenessSeconds, int maximumSpreadTicks, bool enableHistoricalReplay, bool historicalBidAskVerified, bool liveSourceVerified, int markerLookbackBars, int maximumVisibleEvents, bool showOutcomeLabels, bool showStatusPanel, bool saveEventLog, bool saveRawTape, string studyLabel)
		{
			return indicator.EsAbsorptionToxicity(Input, windowSeconds, minimumVolumeAtPrice, minimumPrints, minimumDurationSeconds, minimumAggressorShare, neighborhoodTicks, requireRefillEvidence, cooldownSeconds, rejectionTicks, breachTicks, watchSeconds, adverseMoveTicks, maximumTickGapSeconds, maximumMarkoutLatenessSeconds, maximumSpreadTicks, enableHistoricalReplay, historicalBidAskVerified, liveSourceVerified, markerLookbackBars, maximumVisibleEvents, showOutcomeLabels, showStatusPanel, saveEventLog, saveRawTape, studyLabel);
		}

		public Indicators.EsAbsorptionToxicity EsAbsorptionToxicity(ISeries<double> input , double windowSeconds, double minimumVolumeAtPrice, int minimumPrints, double minimumDurationSeconds, double minimumAggressorShare, int neighborhoodTicks, bool requireRefillEvidence, double cooldownSeconds, int rejectionTicks, int breachTicks, double watchSeconds, int adverseMoveTicks, double maximumTickGapSeconds, double maximumMarkoutLatenessSeconds, int maximumSpreadTicks, bool enableHistoricalReplay, bool historicalBidAskVerified, bool liveSourceVerified, int markerLookbackBars, int maximumVisibleEvents, bool showOutcomeLabels, bool showStatusPanel, bool saveEventLog, bool saveRawTape, string studyLabel)
		{
			return indicator.EsAbsorptionToxicity(input, windowSeconds, minimumVolumeAtPrice, minimumPrints, minimumDurationSeconds, minimumAggressorShare, neighborhoodTicks, requireRefillEvidence, cooldownSeconds, rejectionTicks, breachTicks, watchSeconds, adverseMoveTicks, maximumTickGapSeconds, maximumMarkoutLatenessSeconds, maximumSpreadTicks, enableHistoricalReplay, historicalBidAskVerified, liveSourceVerified, markerLookbackBars, maximumVisibleEvents, showOutcomeLabels, showStatusPanel, saveEventLog, saveRawTape, studyLabel);
		}
	}
}

#endregion
