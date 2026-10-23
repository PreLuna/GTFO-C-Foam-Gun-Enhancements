using System;

namespace PreLuna.FoamGunCustom;

// Contains no Unity references so cadence and resource rules can be tested offline.
internal sealed class ShotScheduler
{
    private double _nextShotTime = double.NegativeInfinity;
    private bool _emitting;

    internal static double CostPerShot(double costOfBullet, double glueEfficiency)
    {
        if (!double.IsFinite(costOfBullet) || costOfBullet <= 0 ||
            !double.IsFinite(glueEfficiency))
            return double.PositiveInfinity;
        // Same minimum denominator as FireBurst in revision 34873.
        return costOfBullet / Math.Max(glueEfficiency, 0.00001);
    }

    internal static bool CanAfford(double available, double cost)
        => double.IsFinite(available) && double.IsFinite(cost) && cost > 0 &&
           available >= cost;

    internal bool TryFire(double now, double interval, bool held,
        double available, double cost, Action emit, Action<double> debit)
    {
        if (!held || _emitting || !double.IsFinite(now) ||
            !double.IsFinite(interval) || interval <= 0 ||
            now < _nextShotTime || !CanAfford(available, cost))
            return false;

        // One shot at most per update. Never queue a catch-up burst after a stall.
        // Releasing/repressing or switching away does not reset this deadline.
        _nextShotTime = now + interval;
        _emitting = true;
        try
        {
            emit();
            debit(cost);
            return true;
        }
        finally { _emitting = false; }
    }
}
