#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Indicators.AUN_Indi;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ATREMASqueezeStrategy : Strategy
    {
        #region Variables
        private ATRTrailStop atrTrailStop;
        private EMA ema21;
        private KeltnerChannel keltnerChannel;
        private AUN_Indi.SqueezeMomentumIndicator squeezeMomentum;
        
        // State tracking variables
        private bool hasSeenPreviousSqueeze = false;
        private bool wasInSqueeze = false;
        private int contractSize = 1;
        private double tickTolerance = 2.0; // 1-2 ticks tolerance for EMA touch
        
        // Trailing stop/profit variables
        private double currentTrailingStop = 0.0;
        private double currentTrailingProfit = 0.0;
        private bool useTrailingStop = true;
        private bool useTrailingProfit = true;
        private double trailingProfitBuffer = 2.0; // Points below upper Keltner for trailing profit
        
        // Parameters
        private int atrPeriod = 9;
        private double atrFactor = 2.9;
        private int emaPeriod = 21;
        private int keltnerEmaPeriod = 21;
        private int keltnerAtrPeriod = 21;
        private double keltnerMultiplier = 3.0;
        private int squeezePeriod = 21;
        private double squeezeKcMultiplier = 1.5;
        private double squeezeBbMultiplier = 2.0;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"ATR EMA Squeeze Strategy with Trailing Stop/Profit for MES/MNQ 2-minute timeframe";
                Name = "ATREMASqueezeStrategy";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 50;
                
                // Strategy parameters
                ContractSize = 1;
                TickTolerance = 2.0;
                UseTrailingStop = true;
                UseTrailingProfit = true;
                TrailingProfitBuffer = 2.0;
            }
            else if (State == State.Configure)
            {
                // Add 2-minute data series if not already primary
                if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 2)
                {
                    AddDataSeries(BarsPeriodType.Minute, 2);
                }
            }
            else if (State == State.DataLoaded)
            {
                // Initialize indicators
                atrTrailStop = ATRTrailStop(atrPeriod, atrFactor, MovingAverageType.Exponential, TrailStopType.Modified);
                ema21 = EMA(emaPeriod);
                keltnerChannel = KeltnerChannel(keltnerEmaPeriod, keltnerMultiplier, keltnerAtrPeriod);
                squeezeMomentum = SqueezeMomentumIndicator(squeezePeriod, squeezeBbMultiplier, squeezePeriod, squeezeKcMultiplier, 
                    Brushes.ForestGreen, Brushes.Red, Brushes.Maroon, Brushes.RoyalBlue, Brushes.MintCream);
                
                // Add indicators to chart
                AddChartIndicator(atrTrailStop);
                AddChartIndicator(ema21);
                AddChartIndicator(keltnerChannel);
                AddChartIndicator(squeezeMomentum);
            }
        }

        protected override void OnBarUpdate()
        {
            // Ensure we have enough bars to trade
            if (CurrentBar < BarsRequiredToTrade)
                return;

            // Check if current instrument is MES or MNQ
            if (!IsSupportedInstrument())
                return;

            // Track squeeze state for "previous squeeze" requirement
            TrackSqueezeState();

            // Check for long entry conditions
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                CheckLongEntry();
            }
            
            // Manage existing positions with trailing stops/profits
            if (Position.MarketPosition == MarketPosition.Long)
            {
                ManageLongPosition();
            }
        }

        private bool IsSupportedInstrument()
        {
            string instrumentName = Instrument.MasterInstrument.Name;
            return instrumentName.Contains("MES") || instrumentName.Contains("MNQ");
        }

        private void TrackSqueezeState()
        {
            // Check if we're currently in a squeeze
            // Your indicator uses dots - when IsSqueezes plot shows a value, we're in squeeze
            bool currentlyInSqueeze = IsInSqueeze();
            
            // If we were in squeeze and now we're not, mark that we've seen a squeeze
            if (wasInSqueeze && !currentlyInSqueeze)
            {
                hasSeenPreviousSqueeze = true;
            }
            
            // Update state
            wasInSqueeze = currentlyInSqueeze;
        }
        
        private bool IsInSqueeze()
        {
            // Check the squeeze state based on Bollinger Bands vs Keltner Channel
            double bbt = Bollinger(squeezeBbMultiplier, squeezePeriod).Upper[0];
            double bbb = Bollinger(squeezeBbMultiplier, squeezePeriod).Lower[0];
            double kct = KeltnerChannel(squeezeKcMultiplier, squeezePeriod).Upper[0];
            double kcb = KeltnerChannel(squeezeKcMultiplier, squeezePeriod).Lower[0];
            
            // Squeeze is on when Bollinger Bands are inside Keltner Channel
            bool sqzOn = (bbb > kcb) && (bbt < kct);
            return sqzOn;
        }

        private void CheckLongEntry()
        {
            // Only trade if we've seen at least one previous squeeze
            if (!hasSeenPreviousSqueeze)
                return;

            // Get current values
            double currentHigh = High[0];
            double currentLow = Low[0];
            double currentClose = Close[0];
            double atrLine = atrTrailStop.Lower[0]; // ATR trail stop line
            double emaValue = ema21[0];
            bool isSqueezeOn = IsInSqueeze(); // Red dot (squeeze active)
            bool isUpAdvance = squeezeMomentum.SqueezeDef[0] > 0; // Positive momentum (light blue)
            
            // Entry Condition 1: Entire candlestick above ATR line
            bool candleAboveATR = currentLow > atrLine;
            
            // Entry Condition 2: 21 EMA above ATR line
            bool emaAboveATR = emaValue > atrLine;
            
            // Entry Condition 3: TTM Squeeze showing light blue (up advance) with red dot
            bool squeezeCondition = isSqueezeOn && isUpAdvance;
            
            // Entry Condition 4: Price touching/intersecting 21 EMA (within 1-2 ticks)
            bool touchingEMA = IsPriceTouchingEMA(currentHigh, currentLow, emaValue);
            
            // Check all conditions
            if (candleAboveATR && emaAboveATR && squeezeCondition && touchingEMA)
            {
                // Calculate initial stop loss: 2 points below ATR line
                double initialStopLoss = atrLine - 2.0;
                
                // Calculate initial take profit: 2 points below upper Keltner line
                double initialTakeProfit = keltnerChannel.Upper[0] - 2.0;
                
                // Enter long position
                EnterLong(ContractSize, "ATR_EMA_Long");
                
                // Initialize trailing levels
                currentTrailingStop = initialStopLoss;
                currentTrailingProfit = initialTakeProfit;
                
                // Set initial stop loss and take profit
                SetStopLoss("ATR_EMA_Long", CalculationMode.Price, initialStopLoss, false);
                SetProfitTarget("ATR_EMA_Long", CalculationMode.Price, initialTakeProfit);
                
                // Print entry details for debugging
                Print(string.Format("Long Entry - Time: {0}, Price: {1}, ATR: {2}, EMA: {3}, Stop: {4}, Target: {5}",
                    Time[0], currentClose, atrLine, emaValue, initialStopLoss, initialTakeProfit));
            }
        }

        private bool IsPriceTouchingEMA(double high, double low, double emaValue)
        {
            // Check if the candlestick intersects with EMA within tick tolerance
            double tolerance = TickTolerance * TickSize;
            
            // EMA is within the high-low range of the candle, or very close to it
            return (low <= emaValue + tolerance && high >= emaValue - tolerance);
        }

        private void ManageLongPosition()
        {
            if (Position.MarketPosition != MarketPosition.Long)
                return;

            double currentPrice = Close[0];
            double atrLine = atrTrailStop.Lower[0];
            double upperKeltner = keltnerChannel.Upper[0];
            
            // Calculate new trailing stop level (2 points below ATR line)
            double newTrailingStop = atrLine - 2.0;
            
            // Calculate new trailing profit level (buffer points below upper Keltner)
            double newTrailingProfit = upperKeltner - TrailingProfitBuffer;
            
            // Update trailing stop (only move up, never down)
            if (UseTrailingStop && newTrailingStop > currentTrailingStop)
            {
                currentTrailingStop = newTrailingStop;
                SetStopLoss("ATR_EMA_Long", CalculationMode.Price, currentTrailingStop, false);
                
                Print(String.Format("Trailing Stop Updated - Time: {0}, New Stop: {1}, ATR Line: {2}",
                    Time[0], currentTrailingStop, atrLine));
            }
            
            // Update trailing profit (only move down as price moves up - tightening the target)
            if (UseTrailingProfit)
            {
                // For trailing profit, we want to move it closer to current price as we profit
                // But we need to make sure we're still profitable
                double minProfitTarget = Position.AveragePrice + 4.0; // Minimum 4 points profit
                
                // Only update if new level is above minimum profit and current price is above the new level
                if (newTrailingProfit > minProfitTarget && currentPrice > newTrailingProfit && 
                    (currentTrailingProfit == 0 || newTrailingProfit < currentTrailingProfit))
                {
                    currentTrailingProfit = newTrailingProfit;
                    SetProfitTarget("ATR_EMA_Long", CalculationMode.Price, currentTrailingProfit);
                    
                    Print(String.Format("Trailing Profit Updated - Time: {0}, New Target: {1}, Upper Keltner: {2}",
                        Time[0], currentTrailingProfit, upperKeltner));
                }
            }
            
            // Optional: Manual exit if price falls below EMA (additional risk management)
            double emaValue = ema21[0];
            if (currentPrice < emaValue)
            {
                ExitLong("ATR_EMA_Long", "EMA_Exit");
                Print(String.Format("Manual Exit - Price below EMA: {0}, EMA: {1}", currentPrice, emaValue));
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            // Handle order updates if needed
            if (order.Name == "ATR_EMA_Long")
            {
                if (orderState == OrderState.Filled)
                {
                    Print(string.Format("Order Filled - {0}: Price {1}, Quantity {2}", order.Name, averageFillPrice, filled));
                }
                else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                {
                    Print(string.Format("Order {0}: {1}", order.Name, orderState));
                }
            }
            
            // Reset trailing levels when position is closed
            if (orderState == OrderState.Filled && (order.OrderAction == OrderAction.SellShort || order.OrderAction == OrderAction.Sell))
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    currentTrailingStop = 0.0;
                    currentTrailingProfit = 0.0;
                }
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contract Size", Description = "Number of contracts to trade per signal", Order = 1, GroupName = "Strategy Parameters")]
        public int ContractSize
        { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Tick Tolerance", Description = "Tolerance in ticks for EMA touch detection", Order = 2, GroupName = "Strategy Parameters")]
        public double TickTolerance
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Trailing Stop", Description = "Enable trailing stop loss based on ATR line", Order = 3, GroupName = "Trailing Parameters")]
        public bool UseTrailingStop
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Trailing Profit", Description = "Enable trailing profit target based on Keltner Channel", Order = 4, GroupName = "Trailing Parameters")]
        public bool UseTrailingProfit
        { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Trailing Profit Buffer", Description = "Points below upper Keltner for trailing profit target", Order = 5, GroupName = "Trailing Parameters")]
        public double TrailingProfitBuffer
        { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "ATR Period", Description = "Period for ATR TrailStop calculation", Order = 6, GroupName = "Indicator Parameters")]
        public int ATRPeriod
        {
            get { return atrPeriod; }
            set { atrPeriod = value; }
        }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "ATR Factor", Description = "Multiplier for ATR TrailStop calculation", Order = 7, GroupName = "Indicator Parameters")]
        public double ATRFactor
        {
            get { return atrFactor; }
            set { atrFactor = value; }
        }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "EMA Period", Description = "Period for 21 EMA", Order = 8, GroupName = "Indicator Parameters")]
        public int EMAPeriod
        {
            get { return emaPeriod; }
            set { emaPeriod = value; }
        }
        #endregion
    }
}