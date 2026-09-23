// EobAtmOrdering.cs
// NinjaTrader 8 strategy. Compile and test on Sim/Playback before permitting live accounts.
#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class EobAtmOrdering : Strategy
    {
        public enum EobEntryType { Market, Limit, StopMarket, StopLimit }
        public enum EobReferencePrice { Open, High, Low, Close, Median, Typical, Weighted, NewBarFirstTick }
        private enum Trigger { None, BuySure, SellSure, BuyDown, SellUp }

        private const string Prefix = "[EOB-ATM] ";
        private Trigger armed;
        private int armedBar;
        private DateTime lastEvaluatedBarTime;
        private string atmId;
        private string entryId;
        private bool createPending;
        private volatile bool terminating;
        private string statusText;
        private Grid panel;
        private TextBlock status;
        private Button buySure, sellSure, buyDown, sellUp, disarm;
        private Button[] quantities;
        private ComboBox buyReference, sellReference, orderType, template;
        private TextBox offset;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "EobAtmOrdering";
                Description = "One ATM entry after a completed bar; use the chart panel to arm the next bar.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                BarsRequiredToTrade = 2;
                IsExitOnSessionCloseStrategy = false;
                IncludeTradeHistoryInBacktest = false;
                StartBehavior = StartBehavior.WaitUntilFlat;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                AtmTemplateName = string.Empty;
                EntryType = EobEntryType.Limit;
                BuyReferencePrice = EobReferencePrice.Low;
                SellReferencePrice = EobReferencePrice.High;
                OffsetTicks = 3;
                OrderQuantity = 1;
                StopLimitLimitOffsetTicks = 0;
                AllowLiveAccounts = false;
                CancelWorkingEntryOnDisarm = true;
            }
            else if (State == State.DataLoaded)
            {
                armed = Trigger.None;
                armedBar = -1;
                statusText = "DISARMED";
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(AddPanel);
            }
            else if (State == State.Realtime)
            {
                SetStatus("DISARMED - ready");
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(AddPanel);
            }
            else if (State == State.Terminated)
            {
                terminating = true;
                Print(Prefix + "Terminated; check native Orders, Strategies and Positions. An ATM may remain active.");
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(RemovePanel);
            }
        }

        protected override void OnBarUpdate()
        {
            if (State != State.Realtime || BarsInProgress != 0)
                return;
            PollEntryStatus();
            if (!IsFirstTickOfBar || CurrentBar < 2 || armed == Trigger.None || createPending)
                return;
            // An arm placed on the first tick of a new bar must not trade the bar
            // that completed BEFORE the operator clicked.
            if (CurrentBar <= armedBar || Time[1] == lastEvaluatedBarTime)
                return;
            lastEvaluatedBarTime = Time[1];
            Trigger requested = armed;
            armed = Trigger.None; // All four buttons are one-shot, including a nonmatching bar.
            bool matches = requested == Trigger.BuySure || requested == Trigger.SellSure
                || (requested == Trigger.BuyDown && Close[1] < Open[1])
                || (requested == Trigger.SellUp && Close[1] > Open[1]);
            if (!matches)
            {
                SetStatus("DISARMED - bar did not match");
                return;
            }
            if (!AccountIsAllowed())
            {
                SetStatus("BLOCKED - simulation account required");
                return;
            }
            SubmitAtm(requested == Trigger.BuySure || requested == Trigger.BuyDown);
        }

        private bool AccountIsAllowed()
        {
            if (AllowLiveAccounts) return true;
            string name = Account == null ? string.Empty : Account.Name;
            bool allowed = name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase);
            if (!allowed) Print(Prefix + "Blocked account '" + name + "'.");
            return allowed;
        }

        private double Reference(bool buy)
        {
            switch (buy ? BuyReferencePrice : SellReferencePrice)
            {
                case EobReferencePrice.Open: return Open[1];
                case EobReferencePrice.High: return High[1];
                case EobReferencePrice.Low: return Low[1];
                case EobReferencePrice.Median: return (High[1] + Low[1]) / 2.0;
                case EobReferencePrice.Typical: return (High[1] + Low[1] + Close[1]) / 3.0;
                case EobReferencePrice.Weighted: return (High[1] + Low[1] + 2 * Close[1]) / 4.0;
                case EobReferencePrice.NewBarFirstTick: return Close[0];
                default: return Close[1];
            }
        }

        private void SubmitAtm(bool buy)
        {
            if (string.IsNullOrWhiteSpace(AtmTemplateName))
            {
                SetStatus("ERROR - select an ATM template");
                return;
            }
            if (createPending || !string.IsNullOrEmpty(atmId) || !string.IsNullOrEmpty(entryId))
            {
                SetStatus("BLOCKED - ATM already tracked; verify native Orders before restarting");
                return;
            }
            if (OrderQuantity < 1)
            {
                SetStatus("ERROR - quantity must be positive");
                return;
            }
            OrderType type = EntryType == EobEntryType.Limit ? OrderType.Limit
                : EntryType == EobEntryType.StopMarket ? OrderType.StopMarket
                : EntryType == EobEntryType.StopLimit ? OrderType.StopLimit : OrderType.Market;
            // Signed offset: +3 is three ticks above the selected reference, for
            // either direction. A market entry cannot have an offset.
            double price = Instrument.MasterInstrument.RoundToTickSize(Reference(buy) + OffsetTicks * TickSize);
            double limit = type == OrderType.Limit || type == OrderType.StopLimit ? price : 0;
            double stop = type == OrderType.StopMarket || type == OrderType.StopLimit ? price : 0;
            if (type == OrderType.StopLimit)
                limit = Instrument.MasterInstrument.RoundToTickSize(stop +
                    (buy ? 1 : -1) * StopLimitLimitOffsetTicks * TickSize);

            string newAtm = GetAtmStrategyUniqueId();
            string newEntry = GetAtmStrategyUniqueId();
            atmId = newAtm;
            entryId = newEntry;
            createPending = true;
            SetStatus("SUBMITTING " + (buy ? "BUY" : "SELL"));
            Print(Prefix + "Create " + type + " " + (buy ? "Buy" : "SellShort") + " quantity=" + OrderQuantity
                + " limit=" + limit + " stop=" + stop + " template='" + AtmTemplateName + "'.");
            try
            {
                // Quantity overload: the selected quantity overrides the ATM template quantity.
                AtmStrategyCreate(buy ? OrderAction.Buy : OrderAction.SellShort,
                    type, limit, stop, OrderQuantity, TimeInForce.Day, newEntry,
                    AtmTemplateName, newAtm,
                    (error, callbackId) =>
                    {
                        if (!terminating)
                            TriggerCustomEvent(o => Created(error, callbackId, newAtm, newEntry), null);
                    });
            }
            catch (Exception ex)
            {
                createPending = false;
                // A thrown call may still have reached the broker. Keep IDs until
                // native Orders are checked rather than silently permitting a retry.
                SetStatus("ERROR - create uncertain; verify native Orders before restarting");
                Print(Prefix + "Create exception: " + ex.Message);
            }
        }

        private void Created(ErrorCode error, string callbackId, string expectedAtm, string expectedEntry)
        {
            if (terminating || atmId != expectedAtm || entryId != expectedEntry) return;
            createPending = false;
            if (error == ErrorCode.NoError && callbackId == expectedAtm)
            {
                SetStatus("ATM CREATED - monitoring entry");
                return;
            }
            Print(Prefix + "Create failed: " + error + ", callback=" + callbackId + ". Verify native Orders.");
            // Preserve IDs in case an entry was accepted despite callback failure.
            SetStatus("ERROR - ATM create: " + error + "; verify Orders");
        }

        private void PollEntryStatus()
        {
            if (string.IsNullOrEmpty(entryId) || createPending) return;
            try
            {
                string[] values = GetAtmStrategyEntryOrderStatus(entryId);
                if (values != null && values.Length >= 3)
                    SetStatus("ATM ENTRY - " + values[2]);
            }
            catch (Exception ex) { Print(Prefix + "Entry status unavailable: " + ex.Message); }
        }

        private void Arm(Trigger request, int quantity, EobEntryType type, int ticks,
            EobReferencePrice buyRef, EobReferencePrice sellRef, string atmTemplate)
        {
            if (State != State.Realtime) { SetStatus("BLOCKED - not realtime"); return; }
            if (createPending || !string.IsNullOrEmpty(atmId) || !string.IsNullOrEmpty(entryId))
            { SetStatus("BLOCKED - ATM already tracked; verify native Orders"); return; }
            if (!AccountIsAllowed()) { SetStatus("BLOCKED - simulation account required"); return; }
            if (string.IsNullOrWhiteSpace(atmTemplate)) { SetStatus("ERROR - enter ATM template name"); return; }
            OrderQuantity = quantity;
            EntryType = type;
            OffsetTicks = ticks;
            BuyReferencePrice = buyRef;
            SellReferencePrice = sellRef;
            AtmTemplateName = atmTemplate.Trim();
            armed = request;
            armedBar = CurrentBar;
            SetStatus("ARMED " + request.ToString().ToUpperInvariant() + " - next completed bar");
        }

        private void Disarm()
        {
            armed = Trigger.None;
            SetStatus("DISARMED");
            if (!CancelWorkingEntryOnDisarm || string.IsNullOrEmpty(entryId)) return;
            try
            {
                AtmStrategyCancelEntryOrder(entryId);
                SetStatus("DISARMED - entry cancel requested; verify Orders");
            }
            catch (Exception ex)
            {
                Print(Prefix + "Cancel failed: " + ex.Message);
                SetStatus("DISARMED - cancel failed; verify Orders");
            }
        }

        private void SetStatus(string value)
        {
            if (statusText == value) return; // Avoid printing every tick while polling.
            statusText = value;
            Print(Prefix + value);
            if (ChartControl != null)
                ChartControl.Dispatcher.InvokeAsync(() => { if (status != null) status.Text = value; });
        }

        private Button Button(string text, Brush color)
        {
            return new Button { Content = text, Background = color, Foreground = Brushes.Black,
                Margin = new Thickness(1), Padding = new Thickness(4, 5, 4, 5), MinWidth = 72 };
        }

        private void Put(UIElement element, int row, int column, int span = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, span);
            panel.Children.Add(element);
        }

        private void AddPanel()
        {
            if (terminating || panel != null || ChartControl == null) return;
            panel = new Grid { HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
                Background = Brushes.White, Width = 304 };
            for (int i = 0; i < 4; i++) panel.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 6; i++) panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            buySure = Button("Buy Sure", Brushes.LightSkyBlue);
            sellSure = Button("Sell Sure", Brushes.Thistle);
            buyDown = Button("Buy Down", Brushes.LightSkyBlue);
            sellUp = Button("Sell Up", Brushes.Thistle);
            Put(buySure, 0, 0, 2); Put(sellSure, 0, 2, 2);
            Put(buyDown, 1, 0, 2); Put(sellUp, 1, 2, 2);
            buySure.Click += BuySureClick; sellSure.Click += SellSureClick;
            buyDown.Click += BuyDownClick; sellUp.Click += SellUpClick;

            int[] sizes = { 1, 2, 5, 20 };
            quantities = new Button[4];
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                quantities[i] = Button(size.ToString(), Brushes.LightGray);
                quantities[i].Tag = size;
                quantities[i].Click += QuantityClick;
                Put(quantities[i], 2, i);
            }
            HighlightQuantity();
            buyReference = new ComboBox { Margin = new Thickness(1), ToolTip = "Buy reference price" };
            sellReference = new ComboBox { Margin = new Thickness(1), ToolTip = "Sell reference price" };
            foreach (EobReferencePrice reference in Enum.GetValues(typeof(EobReferencePrice)))
            {
                buyReference.Items.Add(reference);
                sellReference.Items.Add(reference);
            }
            buyReference.SelectedItem = BuyReferencePrice;
            sellReference.SelectedItem = SellReferencePrice;
            Put(buyReference, 3, 0, 2); Put(sellReference, 3, 2, 2);

            orderType = new ComboBox { Margin = new Thickness(1), ToolTip = "Entry order type" };
            foreach (EobEntryType entry in Enum.GetValues(typeof(EobEntryType))) orderType.Items.Add(entry);
            orderType.SelectedItem = EntryType;
            offset = new TextBox { Text = OffsetTicks.ToString(), Margin = new Thickness(1),
                ToolTip = "Signed offset in ticks (+ is above reference, - is below); market orders ignore offset" };
            Put(orderType, 4, 0, 2); Put(offset, 4, 2, 2);
            template = new ComboBox { IsEditable = true, Text = AtmTemplateName,
                Margin = new Thickness(1), ToolTip = "Type exact name of an existing ATM template" };
            // NinjaScript does not expose a reliable API for enumerating saved ATM templates.
            Put(template, 5, 0, 3);
            disarm = Button("Disarm", Brushes.LightGray);
            disarm.Click += DisarmClick;
            Put(disarm, 5, 3);
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            status = new TextBlock { Text = statusText, Margin = new Thickness(4),
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black };
            Put(status, 6, 0, 4);
            UserControlCollection.Add(panel);
        }

        private void RemovePanel()
        {
            if (panel == null) return;
            buySure.Click -= BuySureClick; sellSure.Click -= SellSureClick;
            buyDown.Click -= BuyDownClick; sellUp.Click -= SellUpClick;
            foreach (Button b in quantities) b.Click -= QuantityClick;
            disarm.Click -= DisarmClick;
            UserControlCollection.Remove(panel);
            panel = null;
            status = null;
        }

        private void HighlightQuantity()
        {
            if (quantities == null) return;
            foreach (Button b in quantities)
                b.Background = (int)b.Tag == OrderQuantity ? Brushes.LimeGreen : Brushes.LightGray;
        }

        private void QuantityClick(object sender, RoutedEventArgs e)
        {
            int selected = (int)((Button)sender).Tag;
            TriggerCustomEvent(o => { if (armed == Trigger.None && string.IsNullOrEmpty(atmId)) OrderQuantity = selected;
                if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(HighlightQuantity); }, null);
        }

        private void ClickArm(Trigger request)
        {
            int ticks;
            if (!int.TryParse(offset.Text, out ticks) || ticks < -10000 || ticks > 10000)
            { status.Text = "ERROR - offset must be -10000..10000 ticks"; return; }
            int quantity = OrderQuantity;
            EobEntryType type = (EobEntryType)orderType.SelectedItem;
            EobReferencePrice buyRef = (EobReferencePrice)buyReference.SelectedItem;
            EobReferencePrice sellRef = (EobReferencePrice)sellReference.SelectedItem;
            string name = template.Text;
            TriggerCustomEvent(o => Arm(request, quantity, type, ticks, buyRef, sellRef, name), null);
        }
        private void BuySureClick(object sender, RoutedEventArgs e) { ClickArm(Trigger.BuySure); }
        private void SellSureClick(object sender, RoutedEventArgs e) { ClickArm(Trigger.SellSure); }
        private void BuyDownClick(object sender, RoutedEventArgs e) { ClickArm(Trigger.BuyDown); }
        private void SellUpClick(object sender, RoutedEventArgs e) { ClickArm(Trigger.SellUp); }
        private void DisarmClick(object sender, RoutedEventArgs e) { TriggerCustomEvent(o => Disarm(), null); }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "ATM template name", GroupName = "ATM", Order = 0)]
        public string AtmTemplateName { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Entry type", GroupName = "Order", Order = 0)]
        public EobEntryType EntryType { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Buy reference price", GroupName = "Order", Order = 1)]
        public EobReferencePrice BuyReferencePrice { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Sell reference price", GroupName = "Order", Order = 2)]
        public EobReferencePrice SellReferencePrice { get; set; }
        [NinjaScriptProperty]
        [Range(-10000, 10000)]
        [Display(Name = "Signed offset ticks", GroupName = "Order", Order = 3)]
        public int OffsetTicks { get; set; }
        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Order quantity", GroupName = "Order", Order = 4)]
        public int OrderQuantity { get; set; }
        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Stop-limit limit offset ticks", GroupName = "Order", Order = 5)]
        public int StopLimitLimitOffsetTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Allow live/non-simulation accounts", Description = "DANGER: permits entries on accounts other than Sim* or Playback*.", GroupName = "Safety", Order = 0)]
        public bool AllowLiveAccounts { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Cancel working entry on disarm", GroupName = "Safety", Order = 1)]
        public bool CancelWorkingEntryOnDisarm { get; set; }
        #endregion
    }
}
