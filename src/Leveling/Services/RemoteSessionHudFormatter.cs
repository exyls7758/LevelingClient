using System;

namespace Multiverse.Services
{
    internal readonly record struct RemoteSessionHudValues(
        double? Fps,
        double? RoundTripMs,
        double? LossPercent,
        double? ReceiveMbps);

    internal static class RemoteSessionHudFormatter
    {
        internal static string Format(RemoteSessionHudValues values)
        {
            string fps = IsValid(values.Fps) ? Math.Round(values.Fps!.Value).ToString("0") : "N/A";
            string lat = IsValid(values.RoundTripMs) ? Math.Round(values.RoundTripMs!.Value).ToString("0") + " ms" : "N/A";
            string loss = IsValid(values.LossPercent) ? values.LossPercent!.Value.ToString("0.0") + "%" : "N/A";
            string rx = IsValid(values.ReceiveMbps) ? values.ReceiveMbps!.Value.ToString("0.0") + " Mbps" : "N/A";
            return $"FPS {fps}  |  LAT {lat}  |  LOSS {loss}  |  RX {rx}";
        }

        private static bool IsValid(double? value) =>
            value.HasValue && double.IsFinite(value.Value) && value.Value >= 0;
    }
}
