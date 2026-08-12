// 
// Copyright (C) 2015, NinjaTrader LLC <www.ninjatrader.com>.
// NinjaTrader reserves the right to modify or overwrite this NinjaScript component with each release.
//
#region Using declarations
using System;
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
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Add this to the declarations. It allows for the use of ArrayLists. (Used in the Level II book creation process.)
using System.Collections.Generic;

// Add this to the declarations. It allows the use of a Timer object to trigger events at our own time.
using System.Windows.Forms;

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators
{
	public class VolumeSignal: Indicator
	{
		
		

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= @"Volume Signal";
				Name						= "VolumeSignal";
				Calculate					= Calculate.OnBarClose;
				DisplayInDataBox			= true;
				DrawOnPricePanel			= false;
				DrawHorizontalGridLines		= true;
				DrawVerticalGridLines		= true;
				PaintPriceMarkers			= true;
				IsDataSeriesRequired		= true;
				ScaleJustification			= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				Period = 20;
				SdFactor = 2.0;
				
				
				AddPlot(new Stroke(Brushes.Black), PlotStyle.Bar, "CurrentVol");
				AddPlot(new Stroke(Brushes.Tomato), PlotStyle.Line, "SDVol");
				AddPlot(new Stroke(Brushes.Snow), PlotStyle.Line,"AvgVol");

			}
			if (State == State.Configure)
			{
				
			}
			if (State == State.Terminated)
			{
				
			}
		}
		
		
		protected override void OnBarUpdate()
		{
			int i=1, i2=1;
			double count=1.0, count2=1.0;
			double sum=0.0;
			double mean=0.0, std=0.0,stdSum=0.0, newVal=0.0;
		
			while (i < (CurrentBar-1) && count < Period+1)
			{
				if (Times[0][0].Hour == Times[0][i].Hour && Times[0][0].Minute == Times[0][i].Minute)
				{
					sum+=System.Convert.ToDouble(Volume[i]);
					count+=1.0;
				}
				i+=1;
			}
			count=count-1.0;
			if (count==Period)
			{
				mean=sum/count;
				while (i2 < (CurrentBar-1) && count2 < Period+1)
				{
					if (Times[0][0].Hour == Times[0][i2].Hour && Times[0][0].Minute == Times[0][i2].Minute)
					{
						newVal=System.Convert.ToDouble(Volume[i2]);
						stdSum+=(newVal-mean)*(newVal-mean);
						count2+=1.0;
					}
					i2+=1;
				}
				count2=count2-1.0;
				if (count2==Period)
				{
					std=Math.Sqrt(stdSum/(count2-1));

					CurrentVol[0]= Volume[0];
					AvgVol[0] = mean;
					SDVol[0] = mean+SdFactor*std;
					
					if (Volume[0] > (mean+SdFactor*std))
					{
						Alert("Volume Alert",Priority.High, Instrument.FullName+": Volume above threshold", "Alert1.wav", 10, Brushes.White, Brushes.Yellow);
						if (Open[0]<=Close[0])
							Draw.Dot(this, "tag_"+CurrentBar.ToString(), true, 0, High[0]+4*TickSize, Brushes.Red,true);
						else
							Draw.Dot(this, "tag_"+CurrentBar.ToString(), true, 0, Low[0]-4*TickSize, Brushes.Blue,true);
					}
				}
			}
			else
			{
				CurrentVol[0] = 0;
				AvgVol[0] = 0;
				SDVol[0] = 0;
			}
		}
		#region Properties
		
		[Range(1, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "Period", GroupName = "NinjaScriptParameters", Order = 0)]
		public int Period
		{ get; set; }
		
		[Range(0.00001, double.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "SdFactor", GroupName = "NinjaScriptParameters", Order = 0)]
		public double SdFactor 
		{ get; set; }
		
		[Browsable(false)]
		[XmlIgnore]
		public Series<double> CurrentVol
		{
			get { return Values[0]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> AvgVol
		{
			get { return Values[2]; }
		}
		
		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SDVol
		{
			get { return Values[1]; }
		}
		
		#endregion

	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private VolumeSignal[] cacheVolumeSignal;
		public VolumeSignal VolumeSignal(int period, double sdFactor)
		{
			return VolumeSignal(Input, period, sdFactor);
		}

		public VolumeSignal VolumeSignal(ISeries<double> input, int period, double sdFactor)
		{
			if (cacheVolumeSignal != null)
				for (int idx = 0; idx < cacheVolumeSignal.Length; idx++)
					if (cacheVolumeSignal[idx] != null && cacheVolumeSignal[idx].Period == period && cacheVolumeSignal[idx].SdFactor == sdFactor && cacheVolumeSignal[idx].EqualsInput(input))
						return cacheVolumeSignal[idx];
			return CacheIndicator<VolumeSignal>(new VolumeSignal(){ Period = period, SdFactor = sdFactor }, input, ref cacheVolumeSignal);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.VolumeSignal VolumeSignal(int period, double sdFactor)
		{
			return indicator.VolumeSignal(Input, period, sdFactor);
		}

		public Indicators.VolumeSignal VolumeSignal(ISeries<double> input , int period, double sdFactor)
		{
			return indicator.VolumeSignal(input, period, sdFactor);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.VolumeSignal VolumeSignal(int period, double sdFactor)
		{
			return indicator.VolumeSignal(Input, period, sdFactor);
		}

		public Indicators.VolumeSignal VolumeSignal(ISeries<double> input , int period, double sdFactor)
		{
			return indicator.VolumeSignal(input, period, sdFactor);
		}
	}
}

#endregion
