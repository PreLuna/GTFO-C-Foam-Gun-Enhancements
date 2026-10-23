using System;
using System.Collections.Generic;
using System.Linq;
using PreLuna.FoamGunCustom;

int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    checks++;
    Console.WriteLine("PASS: " + label);
}

var scheduler = new ShotScheduler();
double ammo = 100;
int emitted = 0;
bool Fire(double now, bool held = true)
    => scheduler.TryFire(now, .08, held, ammo, 2.5, () => emitted++, cost => ammo -= cost);
Check(Fire(0) && emitted == 1 && ammo == 97.5, "first held update emits immediately and charges exactly one pellet");
Check(!Fire(.04, false) && emitted == 1 && ammo == 97.5, "release does not emit or charge queued pellets");
Check(!Fire(.05), "rapid repress cannot bypass the interval");
Check(Fire(.08) && emitted == 2, "next shot becomes available after 80 ms");
Check(Fire(3) && emitted == 3 && !Fire(3), "long frame stall emits only one pellet with no catch-up burst");
Check(!Fire(3.04, false) && !Fire(3.05), "blocked or switched state retains the existing interval");
Check(Fire(3.09), "shooting resumes once allowed and the interval has elapsed");

for (int fps = 30; fps <= 240; fps += 30)
{
    var cadence = new ShotScheduler();
    var times = new List<double>();
    for (int frame = 0; frame < fps * 2; frame++)
    {
        double now = (double)frame / fps;
        cadence.TryFire(now, .08, true, 1000, 1, () => times.Add(now), _ => { });
    }
    Check(times.Zip(times.Skip(1), (a,b) => b-a).All(gap => gap >= .08 - 1e-12),
        $"{fps} FPS never exceeds the configured fire rate");
}

var efficient = new ShotScheduler();
double resource = 10;
int count = 0;
double unitCost = ShotScheduler.CostPerShot(2.5, 2);
for (int step = 0; step < 10; step++)
    efficient.TryFire(step, .08, true, resource, unitCost, () => count++, value => resource -= value);
Check(count == 8 && resource == 0, "GlueEfficiency x2 yields eight pellets from four pellets worth of resources");
Check(ShotScheduler.CostPerShot(.7, 1) == .7, "cost is read from current storage rather than hardcoded");
Check(!ShotScheduler.CanAfford(.5, 1.25), "insufficient fractional ammo cannot produce a free final pellet");
Check(!ShotScheduler.CanAfford(100, ShotScheduler.CostPerShot(0, 1)) &&
      !ShotScheduler.CanAfford(100, ShotScheduler.CostPerShot(double.NaN, 1)) &&
      !ShotScheduler.CanAfford(100, ShotScheduler.CostPerShot(2.5, double.NaN)), "invalid costs and efficiency fail closed");
Check(Math.Abs(ShotScheduler.CostPerShot(2.5, 0) - 250000) < 1e-8, "efficiency denominator uses the native minimum");

var failure = new ShotScheduler();
int debits = 0;
try { failure.TryFire(0, .08, true, 10, 1, () => throw new Exception("emit failed"), _ => debits++); }
catch (Exception error) when (error.Message == "emit failed") { }
Check(debits == 0 && !failure.TryFire(.01, .08, true, 10, 1, () => {}, _ => debits++),
    "failed emission neither debits nor immediately retries");

var reentrant = new ShotScheduler();
bool nestedShot = true;
reentrant.TryFire(0, .08, true, 10, 1,
    () => nestedShot = reentrant.TryFire(1, .08, true, 10, 1, () => {}, _ => {}), _ => {});
Check(!nestedShot, "reentrant update cannot emit a second pellet");

foreach (var scenario in new[] { (Resource: 100f, Efficiency: 1d, Count: 200),
                                (Resource: 150f, Efficiency: 1d, Count: 300),
                                (Resource: 150f, Efficiency: 2d, Count: 600) })
{
    var lowCost = new ShotScheduler();
    float remaining = scenario.Resource;
    int fired = 0;
    double pelletCost = ShotScheduler.CostPerShot(.5, scenario.Efficiency);
    for (int step = 0; step <= scenario.Count; step++)
        lowCost.TryFire(step, .08, true, remaining, pelletCost,
            () => fired++, value => remaining -= (float)value);
    Check(fired == scenario.Count && remaining == 0,
        $"0.5 base cost: {scenario.Resource} resources with efficiency {scenario.Efficiency} yields {scenario.Count} pellets and stops at zero");
}
Console.WriteLine($"All {checks} checks passed.");
