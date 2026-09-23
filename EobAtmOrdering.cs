// EobAtmOrdering.cs
// Original NinjaTrader 8 strategy. Compile and validate inside NinjaTrader 8.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class EobAtmOrdering : Strategy
    {
        public enum EobSignalMode
        {
            Unconditional,
            BarBody,
            CloseOutsidePreviousRange
        }

        public enum EobEntryType
        {
            Market,
            Limit,
            StopMarket,
            StopLimit
        }

        public enum EobReferencePrice
        {
            Open,
            High,
            Low,
            Close,
            Median,
            Typical,
            Weighted,
            NewBarFirstTick
        }

        private enum ArmMode
        {
            Disarmed,
            Long,
            Short,
            Auto
        }

        private const string LogPrefix = "[EOB-ATM] ";
        private ArmMode armMode;
        private string atmStrategyId;
        private string entryOrderId;
        private string statusText;
        private DateTime lastEvaluatedBarTime;
        private bool createPending;
        private bool terminationStarted;

        private Grid controlPanel;
        private TextBlock statusBlock;
        private Button armLongButton;
        private Button armShortButton;
        private Button armAutoButton;
        private Button disarmButton;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                                = "EobAtmOrdering";
                Description                         = "Arms an ATM template entry after a bar is confirmed complete.";
                Calculate                           = Calculate.OnEachTick;
                IsOverlay                           = true;
                IsUnmanaged                         = false;
                EntriesPerDirection                 = 1;
                EntryHandling                       = EntryHandling.AllEntries;
                DefaultQuantity                     = 1;
                BarsRequiredToTrade                 = 2;
                IsExitOnSessionCloseStrategy        = false;
                IncludeTradeHistoryInBacktest       = false;
                StartBehavior                       = StartBehavior.WaitUntilFlat;
                RealtimeErrorHandling               = RealtimeErrorHandling.StopCancelClose;

                AtmTemplateName                     = string.Empty;
                SignalMode                          = EobSignalMode.Unconditional;
                EntryType                           = EobEntryType.Market;
                ReferencePrice                      = EobReferencePrice.Close;
                OffsetTicks                         = 0;
                StopLimitLimitOffsetTicks            = 0;
                ActiveUntilTriggered                = false;
                AllowLiveAccounts                   = false;
                CancelWorkingEntryOnDisarm          = true;
                EnableSoundAlert                    = false;
                AlertSoundFile                      = string.Empty;
            }
            else if (State == State.DataLoaded)
            {
                armMode = ArmMode.Disarmed;
                statusText = "DISARMED";
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(AddChartControls);
            }
            else if (State == State.Realtime)
            {
                SetStatus("DISARMED - realtime ready");
            }
            else if (State == State.Terminated)
            {
                terminationStarted = true;
                Print(LogPrefix + "Terminating. Verify native Orders, Strategies, and Positions; a broker-held ATM may remain active.");
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(RemoveChartControls);
            }
        }

        protected override void OnBarUpdate()
        {
            if (State != State.Realtime || BarsInProgress != 0)
                return;

            PollEntryStatus();

            if (!IsFirstTickOfBar || CurrentBar < 2 || armMode == ArmMode.Disarmed || createPending)
                return;

            DateTime completedBarTime = Time[1];
            if (completedBarTime == lastEvaluatedBarTime)
                return;

            // Store before any order work so duplicate ticks/callbacks cannot create twice.
            lastEvaluatedBarTime = completedBarTime;

            MarketPosition candidate = DetermineCandidateDirection();
            bool shouldSubmit = CandidateMatchesArm(candidate);

            if (!ActiveUntilTriggered)
                armMode = ArmMode.Disarmed;

            if (!shouldSubmit)
            {
                SetStatus(ActiveUntilTriggered
                    ? "ARMED " + armMode.ToString().ToUpperInvariant() + " - waiting for matching bar"
                    : "DISARMED - completed bar did not match");
                return;
            }

            if (!AccountIsAllowed())
            {
                armMode = ArmMode.Disarmed;
                SetStatus("BLOCKED - use Sim/Playback or explicitly allow live accounts");
                return;
            }

            SubmitAtm(candidate);
        }

        private MarketPosition DetermineCandidateDirection()
        {
            if (SignalMode == EobSignalMode.Unconditional)
            {
                if (armMode == ArmMode.Long)
                    return MarketPosition.Long;
                if (armMode == ArmMode.Short)
                    return MarketPosition.Short;
                return MarketPosition.Flat;
            }

            if (SignalMode == EobSignalMode.BarBody)
            {
                if (Close[1] > Open[1])
                    return MarketPosition.Long;
                if (Close[1] < Open[1])
                    return MarketPosition.Short;
                return MarketPosition.Flat;
            }

            if (Close[1] > High[2])
                return MarketPosition.Long;
            if (Close[1] < Low[2])
                return MarketPosition.Short;
            return MarketPosition.Flat;
        }

        private bool CandidateMatchesArm(MarketPosition candidate)
        {
            if (candidate == MarketPosition.Flat)
                return false;
            if (armMode == ArmMode.Auto)
                return true;
            return (armMode == ArmMode.Long && candidate == MarketPosition.Long)
                || (armMode == ArmMode.Short && candidate == MarketPosition.Short);
        }

        private bool AccountIsAllowed()
        {
            if (AllowLiveAccounts)
                return true;

            string accountName = Account == null ? string.Empty : Account.Name;
            bool allowed = accountName.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                || accountName.StartsWith("Playback", StringComparison.OrdinalIgnoreCase);

            if (!allowed)
                Print(LogPrefix + "Account blocked by safety setting. Account='" + (string.IsNullOrEmpty(accountName) ? "<unknown>" : accountName) + "'.");
            return allowed;
        }

        private void SubmitAtm(MarketPosition direction)
        {
            if (string.IsNullOrWhiteSpace(AtmTemplateName))
            {
                armMode = ArmMode.Disarmed;
                SetStatus("ERROR - ATM template name is empty");
                return;
            }

            if (!string.IsNullOrEmpty(atmStrategyId) || !string.IsNullOrEmpty(entryOrderId))
            {
                armMode = ArmMode.Disarmed;
                SetStatus("BLOCKED - this instance already tracks an ATM; reload after native verification");
                return;
            }

            OrderAction action = direction == MarketPosition.Long ? OrderAction.Buy : OrderAction.SellShort;
            OrderType orderType = MapOrderType();
            double reference = GetReferencePrice();
            double entryPrice = RoundPrice(reference + OffsetTicks * TickSize);
            double limitPrice = 0;
            double stopPrice = 0;

            if (orderType == OrderType.Limit)
                limitPrice = entryPrice;
            else if (orderType == OrderType.StopMarket)
                stopPrice = entryPrice;
            else if (orderType == OrderType.StopLimit)
            {
                stopPrice = entryPrice;
                double directionalLimitOffset = Math.Abs(StopLimitLimitOffsetTicks) * TickSize;
                limitPrice = RoundPrice(stopPrice + (direction == MarketPosition.Long ? directionalLimitOffset : -directionalLimitOffset));
            }

            string newAtmId = GetAtmStrategyUniqueId();
            string newEntryId = GetAtmStrategyUniqueId();
            atmStrategyId = newAtmId;
            entryOrderId = newEntryId;
            createPending = true;
            SetStatus("SUBMITTING " + direction.ToString().ToUpperInvariant());

            Print(LogPrefix + "Creating ATM. Direction=" + direction
                + ", Type=" + orderType
                + ", Limit=" + limitPrice
                + ", Stop=" + stopPrice
                + ", Template='" + AtmTemplateName + "'.");

            try
            {
                AtmStrategyCreate(
                    action,
                    orderType,
                    limitPrice,
                    stopPrice,
                    TimeInForce.Day,
                    newEntryId,
                    AtmTemplateName,
                    newAtmId,
                    (errorCode, callbackId) =>
                    {
                        if (terminationStarted)
                            return;

                        TriggerCustomEvent(o => ProcessCreateCallback(errorCode, callbackId, newAtmId, newEntryId), null);
                    });
            }
            catch (Exception ex)
            {
                createPending = false;
                armMode = ArmMode.Disarmed;
                atmStrategyId = string.Empty;
                entryOrderId = string.Empty;
                SetStatus("ERROR - ATM create threw; see Output");
                Print(LogPrefix + "AtmStrategyCreate exception: " + ex.Message);
            }
        }

        private void ProcessCreateCallback(ErrorCode errorCode, string callbackId, string expectedAtmId, string expectedEntryId)
        {
            if (terminationStarted || expectedAtmId != atmStrategyId || expectedEntryId != entryOrderId)
                return;

            createPending = false;
            if (errorCode == ErrorCode.NoError && callbackId == expectedAtmId)
            {
                SetStatus("ATM CREATED - monitoring entry");
                Notify("ATM created");
                return;
            }

            Print(LogPrefix + "ATM create failed. Error=" + errorCode + ", CallbackId='" + callbackId + "'.");
            armMode = ArmMode.Disarmed;
            atmStrategyId = string.Empty;
            entryOrderId = string.Empty;
            SetStatus("ERROR - ATM create: " + errorCode);
            Notify("ATM creation failed: " + errorCode);
        }

        private void PollEntryStatus()
        {
            if (string.IsNullOrEmpty(entryOrderId) || createPending)
                return;

            try
            {
                string[] values = GetAtmStrategyEntryOrderStatus(entryOrderId);
                if (values == null || values.Length < 3)
                    return;

                string orderState = values[2];
                SetStatus("ATM ENTRY - " + orderState);

                if (string.Equals(orderState, "Rejected", StringComparison.OrdinalIgnoreCase))
                    Print(LogPrefix + "ATM entry rejected. Inspect NinjaTrader Orders and Log tabs.");
            }
            catch (Exception ex)
            {
                // Queries may transiently fail during connection/order-state transitions.
                Print(LogPrefix + "Entry status query unavailable: " + ex.Message);
            }
        }

        private OrderType MapOrderType()
        {
            if (EntryType == EobEntryType.Limit)
                return OrderType.Limit;
            if (EntryType == EobEntryType.StopMarket)
                return OrderType.StopMarket;
            if (EntryType == EobEntryType.StopLimit)
                return OrderType.StopLimit;
            return OrderType.Market;
        }

        private double GetReferencePrice()
        {
            switch (ReferencePrice)
            {
                case EobReferencePrice.Open:
                    return Open[1];
                case EobReferencePrice.High:
                    return High[1];
                case EobReferencePrice.Low:
                    return Low[1];
                case EobReferencePrice.Median:
                    return (High[1] + Low[1]) / 2.0;
                case EobReferencePrice.Typical:
                    return (High[1] + Low[1] + Close[1]) / 3.0;
                case EobReferencePrice.Weighted:
                    return (High[1] + Low[1] + 2.0 * Close[1]) / 4.0;
                case EobReferencePrice.NewBarFirstTick:
                    return Close[0];
                default:
                    return Close[1];
            }
        }

        private double RoundPrice(double price)
        {
            return Instrument.MasterInstrument.RoundToTickSize(price);
        }

        private void Arm(ArmMode requestedMode)
        {
            if (State != State.Realtime)
            {
                SetStatus("BLOCKED - strategy is not realtime");
                return;
            }

            if (createPending || !string.IsNullOrEmpty(atmStrategyId) || !string.IsNullOrEmpty(entryOrderId))
            {
                SetStatus("BLOCKED - an ATM/order is already tracked");
                return;
            }

            if (!AccountIsAllowed())
            {
                SetStatus("BLOCKED - account safety check failed");
                return;
            }

            armMode = requestedMode;
            SetStatus("ARMED " + requestedMode.ToString().ToUpperInvariant());
            Notify("EOB ordering armed " + requestedMode);
        }

        private void Disarm()
        {
            armMode = ArmMode.Disarmed;
            SetStatus("DISARMED");

            if (!CancelWorkingEntryOnDisarm || string.IsNullOrEmpty(entryOrderId))
                return;

            try
            {
                AtmStrategyCancelEntryOrder(entryOrderId);
                Print(LogPrefix + "Requested cancellation of tracked ATM entry. Verify result in native Orders UI.");
                SetStatus("DISARMED - entry cancel requested");
            }
            catch (Exception ex)
            {
                Print(LogPrefix + "Entry cancellation request failed: " + ex.Message);
                SetStatus("DISARMED - cancel failed; verify native Orders UI");
            }
        }

        private void SetStatus(string value)
        {
            statusText = value;
            Print(LogPrefix + value);

            if (ChartControl == null || statusBlock == null)
                return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (statusBlock != null)
                    statusBlock.Text = statusText;
            });
        }

        private void Notify(string message)
        {
            if (!EnableSoundAlert || string.IsNullOrWhiteSpace(AlertSoundFile))
                return;

            try
            {
                PlaySound(AlertSoundFile);
            }
            catch (Exception ex)
            {
                Print(LogPrefix + "Sound alert failed: " + ex.Message);
            }
        }

        private Button MakeButton(string caption, Brush background)
        {
            return new Button
            {
                Content = caption,
                Margin = new Thickness(2),
                Padding = new Thickness(5, 2, 5, 2),
                Background = background,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                MinWidth = 78
            };
        }

        private void AddChartControls()
        {
            if (controlPanel != null || ChartControl == null)
                return;

            controlPanel = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                Background = new SolidColorBrush(Color.FromArgb(220, 28, 28, 30))
            };
            controlPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            controlPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < 4; i++)
                controlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            armLongButton = MakeButton("ARM LONG", Brushes.SeaGreen);
            armShortButton = MakeButton("ARM SHORT", Brushes.Firebrick);
            armAutoButton = MakeButton("ARM AUTO", Brushes.SteelBlue);
            disarmButton = MakeButton("DISARM", Brushes.DimGray);

            armLongButton.Click += ArmLongClick;
            armShortButton.Click += ArmShortClick;
            armAutoButton.Click += ArmAutoClick;
            disarmButton.Click += DisarmClick;

            Grid.SetColumn(armLongButton, 0);
            Grid.SetColumn(armShortButton, 1);
            Grid.SetColumn(armAutoButton, 2);
            Grid.SetColumn(disarmButton, 3);
            controlPanel.Children.Add(armLongButton);
            controlPanel.Children.Add(armShortButton);
            controlPanel.Children.Add(armAutoButton);
            controlPanel.Children.Add(disarmButton);

            statusBlock = new TextBlock
            {
                Text = statusText,
                Foreground = Brushes.White,
                Margin = new Thickness(5, 2, 5, 5),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(statusBlock, 1);
            Grid.SetColumnSpan(statusBlock, 4);
            controlPanel.Children.Add(statusBlock);

            UserControlCollection.Add(controlPanel);
        }

        private void RemoveChartControls()
        {
            if (controlPanel == null)
                return;

            armLongButton.Click -= ArmLongClick;
            armShortButton.Click -= ArmShortClick;
            armAutoButton.Click -= ArmAutoClick;
            disarmButton.Click -= DisarmClick;
            UserControlCollection.Remove(controlPanel);
            controlPanel = null;
            statusBlock = null;
            armLongButton = null;
            armShortButton = null;
            armAutoButton = null;
            disarmButton = null;
        }

        private void ArmLongClick(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(o => Arm(ArmMode.Long), null);
        }

        private void ArmShortClick(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(o => Arm(ArmMode.Short), null);
        }

        private void ArmAutoClick(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(o => Arm(ArmMode.Auto), null);
        }

        private void DisarmClick(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(o => Disarm(), null);
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "ATM template name", GroupName = "ATM", Order = 0)]
        public string AtmTemplateName { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Signal mode", GroupName = "Signal", Order = 0)]
        public EobSignalMode SignalMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Entry type", GroupName = "Order", Order = 0)]
        public EobEntryType EntryType { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reference price", GroupName = "Order", Order = 1)]
        public EobReferencePrice ReferencePrice { get; set; }

        [NinjaScriptProperty]
        [Range(-10000, 10000)]
        [Display(Name = "Signed offset ticks", GroupName = "Order", Order = 2)]
        public int OffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Stop-limit limit offset ticks", GroupName = "Order", Order = 3)]
        public int StopLimitLimitOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Active until triggered", GroupName = "Signal", Order = 1)]
        public bool ActiveUntilTriggered { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow live/non-simulation accounts", Description = "DANGER: permits order creation on accounts not named Sim* or Playback*.", GroupName = "Safety", Order = 0)]
        public bool AllowLiveAccounts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cancel working entry on disarm", GroupName = "Safety", Order = 1)]
        public bool CancelWorkingEntryOnDisarm { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable sound alert", GroupName = "Alerts", Order = 0)]
        public bool EnableSoundAlert { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sound file", Description = "Full path to a WAV file accessible to NinjaTrader.", GroupName = "Alerts", Order = 1)]
        public string AlertSoundFile { get; set; }
        #endregion
    }
}
