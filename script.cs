using System.Collections.Generic;
using UnityEngine;

namespace PPGPerformanceSuite
{
    // Overall performance pass. Nothing here changes damage, range, speed,
    // or any other gameplay value - it only changes HOW the engine computes
    // things, not WHAT it computes. Main pieces:
    //
    // - Auto Optimizer: watches FPS and smoothly scales everything below
    //   into strain as things get heavy, easing back to normal once they
    //   recover (debris/decal/corpse caps, physics iterations, and as a
    //   last resort the game's own visual settings)
    // - Bigger object pools (stops tracers/effects from silently
    //   disappearing during heavy fire)
    // - Smart bullet collision (every bullet starts cheap, only goes
    //   expensive when something hittable is actually nearby)
    // - Off-screen visibility culling for bleeding particles and fire mesh
    //   generation (pauses expensive per-frame work nobody can see)
    // - Debris/decal/corpse cleanup with off-screen-first priority and
    //   simplified collision shapes on debris
    // - A session-long cache remembering which spawned item types have
    //   none of the above systems at all, skipping redundant checks
    // - Incremental garbage collection to soften stutter from allocations
    // - A rare last-resort audio safety net for truly extreme simultaneous
    //   sound counts
    public class Mod : MonoBehaviour
    {
        public static void Main()
        {
            TunePhysicsSolver();
            EnableIncrementalGC();
            HookAllSpawnBehaviours();
            HookCorpseLimiter();

            GameObject bootstrap = new GameObject("PPGPerformanceSuite_Bootstrap");
            bootstrap.AddComponent<AutoOptimizer>();
            bootstrap.AddComponent<PoolBooster>();
            bootstrap.AddComponent<DecalLimiter>();
            bootstrap.AddComponent<DebrisLimiter>();
            bootstrap.AddComponent<AutoVelocityIterationTuner>();
            bootstrap.AddComponent<AutoOptimizerVisualLock>();
            bootstrap.AddComponent<WelcomePopup>();
            bootstrap.AddComponent<AudioSafetyCap>();

            // EXPERIMENTAL - testing whether allowing jointed/welded
            // contraptions to sleep too (not just loose debris) helps or
            // hurts. If anything looks wrong (stretchy welds, stuff not
            // waking up properly), just comment out this one line and
            // restart the game to fully revert - nothing else in the
            // suite depends on this.
            bootstrap.AddComponent<IdleRigidbodySleeper>();
            bootstrap.AddComponent<SettingsMenuNoteInjector>();

            ModAPI.Notify("PPG Performance Suite loaded");
        }

        // THE CACHE THAT BUILDS UP OVER TIME: every single thing that
        // spawns - a brick, a screw, a wall, doesn't matter - used to
        // trigger FOUR separate full GetComponentsInChildren tree-walks
        // (launchers, circulation, bleeding, fire), even though the vast
        // majority of spawned objects have none of these systems at all.
        // We can't skip the search for asset types that DO have one of
        // these (we still need the actual component instance on THIS
        // specific spawn to hook it), but we CAN remember, the moment we
        // first see a given SpawnableAsset, which of the four systems it
        // structurally has at all - and for the ones it doesn't have,
        // skip that search completely on every future spawn of that same
        // asset for the rest of the session.
        //
        // This is the closest real equivalent in PP to a shader pipeline
        // cache: first use of a given "type" pays full cost and remembers
        // what it learned, every later use of that same type gets to skip
        // the parts of the work already known to be unnecessary. It only
        // ever grows more accurate over a session, never goes stale in a
        // way that could miss something (a "has it" answer is always
        // re-verified by actually running the search; only a confirmed
        // "definitely doesn't have it" answer is trusted to skip a future
        // search).
        private static void HookAllSpawnBehaviours()
        {
            ModAPI.OnItemSpawned += (sender, args) =>
            {
                if (args.Instance == null)
                {
                    return;
                }

                SpawnableAsset asset = args.SpawnableAsset;
                SpawnStructureCache.ComponentPresence known = default(SpawnStructureCache.ComponentPresence);
                bool haveCacheEntry = asset != null && SpawnStructureCache.TryGet(asset, out known);

                bool foundLauncher = false;
                bool foundCirculation = false;
                bool foundBleeding = false;
                bool foundFire = false;

                if (!haveCacheEntry || known.HasLaunchers)
                {
                    foreach (ProjectileLauncherBehaviour launcher in args.Instance.GetComponentsInChildren<ProjectileLauncherBehaviour>())
                    {
                        foundLauncher = true;
                        launcher.OnLaunch += (s, projectile) =>
                        {
                            if (projectile != null)
                            {
                                projectile.AddComponent<SmartBulletCollision>();
                            }
                        };
                    }
                }

                if (!haveCacheEntry || known.HasCirculation)
                {
                    foreach (CirculationBehaviour cb in args.Instance.GetComponentsInChildren<CirculationBehaviour>(includeInactive: true))
                    {
                        foundCirculation = true;
                        // Repairs old saves from a previous mod version that
                        // could leave circulation disabled (see note below).
                        if (!cb.enabled)
                        {
                            cb.enabled = true;
                        }
                    }
                }

                if (!haveCacheEntry || known.HasBleeding)
                {
                    foreach (BleedingParticleBehaviour bp in args.Instance.GetComponentsInChildren<BleedingParticleBehaviour>(includeInactive: true))
                    {
                        foundBleeding = true;
                        bp.gameObject.AddComponent<BleedingParticleCuller>();
                    }
                }

                if (!haveCacheEntry || known.HasFire)
                {
                    foreach (FireMeshBehaviour fire in args.Instance.GetComponentsInChildren<FireMeshBehaviour>(includeInactive: true))
                    {
                        foundFire = true;
                        fire.gameObject.AddComponent<FireMeshCuller>();
                    }
                }

                if (asset != null && !haveCacheEntry)
                {
                    SpawnStructureCache.Record(asset, new SpawnStructureCache.ComponentPresence
                    {
                        HasLaunchers = foundLauncher,
                        HasCirculation = foundCirculation,
                        HasBleeding = foundBleeding,
                        HasFire = foundFire
                    });
                }
            };
        }

        // Every dead body left in the world keeps costing CPU forever - it's
        // still a full ragdoll (multiple rigidbodies + joints), still has
        // blood circulation state, and never gets cleaned up by the base
        // game. Over a long session with lots of casualties, corpses just
        // keep piling up. This tracks bodies as they die (via ModAPI.OnDeath,
        // which is a real event - no scanning needed at all) and once the
        // count crosses the cap, quietly removes the oldest corpses first.
        // Only ever touches confirmed-dead bodies, never anyone still alive.
        private static void HookCorpseLimiter()
        {
            ModAPI.OnDeath += (sender, person) =>
            {
                CorpseLimiter.Track(person);
            };
        }

        // NOTE: Off-screen pose-update throttling was considered here and
        // rejected. PersonBehaviour.LateUpdate() calls DetermineActivePose()
        // every frame, but that method call can't be isolated from outside
        // the compiled game code - the only lever available is enabling/
        // disabling the WHOLE PersonBehaviour component, which almost
        // certainly also gates health, interaction, and other gameplay
        // logic beyond just pose selection. That's the same class of risk
        // as the circulation bug earlier, but with a much bigger blast
        // radius since PersonBehaviour is the master controller for the
        // whole human. Not worth it without a way to patch just the one
        // method, which isn't available at the mod level.

        // "Use more threads" isn't really achievable for PP's game logic -
        // Unity's MonoBehaviour Update/FixedUpdate and its 2D physics solve
        // all run on the main thread by design, that's core to how Unity
        // scripting works and isn't something a mod can retrofit. BUT there
        // is one real, legitimate lever that's closely related: Unity's
        // incremental garbage collector. Normally the GC does one big
        // "stop everything, clean up" pass, which is exactly the kind of
        // hitch we traced to repeated allocations earlier this session
        // (FindObjectsOfType calls, Instantiate/Destroy churn from bullets,
        // etc). Incremental mode spreads that cleanup work across many
        // small slices over multiple frames instead of one big pause, which
        // should soften stutter without changing any actual game behavior.
        private static void EnableIncrementalGC()
        {
            UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
        }

        // Unity's 2D solver defaults (8 velocity / 3 position iterations)
        // are tuned for general-purpose physics. PP contraptions are mostly
        // rigid welded structures, not loose stacks of objects relying on
        // high solver accuracy, so we can safely cut iteration counts for
        // a meaningful CPU saving with no visible behavior difference.
        private static void TunePhysicsSolver()
        {
            // Reverted: lowering velocity/position iterations made welded
            // joints soft and bouncy since the solver no longer converges
            // tightly enough on rigid constraints. Not worth the tradeoff.
            Physics2D.reuseCollisionCallbacks = true;

            // Reverted: heavier structures (large ships with many joints
            // under load, like Red Sky) take longer to fully settle.
            // Forcing them to sleep sooner while still under joint tension
            // caused them to get jolted awake repeatedly by nearby
            // activity, which looked like stretchy/bouncy welds. Lighter
            // builds (Hawkeye) never hit this edge case, but it's not
            // worth the tradeoff for big contraptions.
            // Physics2D.timeToSleep = 0.25f;
        }
    }

    // NOTE: A circulation-throttling optimization (pausing blood simulation
    // on stable/idle limbs) was attempted here and removed. PP's ModAPI has
    // no unload/disable event, so a mod has no reliable way to know when it
    // gets toggled off in the menu - any GameObjects/components it created
    // keep running regardless of the UI state. That caused humans to take
    // damage incorrectly (random deaths) even after the mod was "disabled."
    // Not worth the risk versus the FPS gain. If revisited, it would need
    // PP to expose a real unload hook first.

    // BUG FIX: PP's undo system (UndoControllerBehaviour) tracks actions in
    // a positional list. When something we destroy directly was originally
    // spawned through the normal flow, it can still have a stale entry
    // sitting in that list - pressing undo afterward steps to the wrong
    // position (reported as "undo deletes the object before the last one").
    // The game exposes exactly the right API for this: find any action
    // related to the object, deregister it, THEN destroy. Every cleanup
    // system in this mod should go through this instead of calling
    // Object.Destroy directly.
    public static class SafeDestroy
    {
        public static void Do(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            if (UndoControllerBehaviour.FindRelevantAction(go, out IUndoableAction action))
            {
                UndoControllerBehaviour.DeregisterAction(action);
            }

            Object.Destroy(go);
        }
    }

    // The shared "Auto Optimizer" system. One central FPS monitor that other
    // pieces (debris, decals, corpses) check in on, instead of each one
    // tracking FPS separately. Exposes a smooth 0-1 StrainLevel instead of
    // a hard on/off switch: fully relaxed (0) at FullNormalFps or above,
    // gradually tightening as FPS drops, reaching MAX STRAIN (1) at
    // MaxStrainFps. Caps scale proportionally along that curve rather than
    // snapping between two fixed states. Smoothed over a rolling window +
    // an extra lerp on the exposed value itself so caps ease in and out
    // instead of jumping. Only ever affects disposable stuff (debris,
    // blood decals, dead bodies) - never anything still alive or part of
    // active gameplay.
    public class AutoOptimizer : MonoBehaviour
    {
        // BUG FIX: was 65f. Most players on a standard 60Hz monitor don't
        // perceive anything in the 50-65 range as "laggy" at all - it's a
        // genuinely valid subjective experience, not a bug in their
        // perception. The old threshold meant even a brief dip to the low
        // 50s could start visibly degrading things (Fancy Effects off
        // within seconds) despite the player feeling completely smooth.
        // Lowering this gives real headroom before ANY visual change
        // happens - strain now only starts building once FPS is low
        // enough that most people would actually notice something's off.
        private const float FullNormalFps = 50f;  // at or above this, StrainLevel = 0 (fully relaxed)
        private const float MaxStrainFps = 30f;   // at or below this, StrainLevel = 1 (MAX STRAIN)
        private const float SampleWindow = 2f;
        private const float SmoothingRate = 0.5f; // how much the exposed value moves toward target each window

        // Threshold for the small "kicked in" notification below - not the
        // same as any individual visual-setting threshold, just "is it
        // doing anything at all right now." A small dead zone above 0
        // avoids firing right at the boundary from normal FPS jitter.
        private const float ActiveNotifyThreshold = 0.05f;

        public static float StrainLevel { get; private set; }

        // Exposed for AutoVelocityIterationTuner, which scales physics
        // iterations UP above this same "relaxed" threshold, not just
        // down below it. Smoothed the same way StrainLevel is.
        public static float SmoothedFps { get; private set; }

        private float rollingTime;
        private int rollingFrames;
        private bool wasActive;

        private void Update()
        {
            rollingTime += Time.unscaledDeltaTime;
            rollingFrames++;

            if (rollingTime < SampleWindow)
            {
                return;
            }

            float avgFps = rollingFrames / rollingTime;
            rollingTime = 0f;
            rollingFrames = 0;

            SmoothedFps = Mathf.Lerp(SmoothedFps, avgFps, SmoothingRate);

            // 0 at FullNormalFps or above, 1 at MaxStrainFps or below,
            // smoothly interpolated in between.
            float target = Mathf.InverseLerp(FullNormalFps, MaxStrainFps, avgFps);

            StrainLevel = Mathf.Lerp(StrainLevel, target, SmoothingRate);

            // Small bottom-corner heads-up, same style as the "mod loaded"
            // message on launch - only fires on the moment it actually
            // starts or stops doing something, never spams every frame
            // while active.
            bool isActive = StrainLevel > ActiveNotifyThreshold;
            if (isActive != wasActive)
            {
                wasActive = isActive;
                ModAPI.Notify(isActive
                    ? "Auto Optimizer active"
                    : "Auto Optimizer relaxed - performance back to normal");
            }
        }
    }

    // DebrisComponent is created internally inside the base game's shatter
    // code (not through ModAPI.OnItemSpawned), so we can't catch it the
    // instant it's created the cheap way we do for regular spawnables.
    // The original version compensated by scanning the whole scene with
    // FindObjectsOfType every 0.3s - that's a fresh full-scene allocation
    // 3+ times a second, which is exactly the kind of repeated GC pressure
    // that causes periodic stutter during sustained combat. Debris bursts
    // happen in short, obvious windows (an explosion or collapse just
    // happened), not continuously, so scanning far less often still catches
    // every burst while cutting allocation frequency drastically.
    public class DebrisLimiter : MonoBehaviour
    {
        private const int MaxDebrisPieces = 120;
        private const int MaxDebrisPiecesStrained = 60; // tighter cap when Auto Optimizer detects strain
        private const float ScanInterval = 0.3f;

        // How slow something needs to be moving before we consider it
        // "settled enough" to force asleep early while off-screen. Loose
        // enough to catch debris that's basically done bouncing, tight
        // enough that nothing still actively flying gets frozen mid-arc.
        private const float SleepVelocityThreshold = 0.05f;

        private readonly Queue<DebrisComponent> tracked = new Queue<DebrisComponent>();
        private readonly HashSet<DebrisComponent> trackedSet = new HashSet<DebrisComponent>();

        private float timer;

        private void Update()
        {
            timer += Time.deltaTime;
            if (timer < ScanInterval)
            {
                return;
            }
            timer = 0f;

            DebrisComponent[] current = Object.FindObjectsOfType<DebrisComponent>();
            foreach (DebrisComponent d in current)
            {
                if (d == null || trackedSet.Contains(d))
                {
                    continue;
                }

                trackedSet.Add(d);
                tracked.Enqueue(d);

                // First time seeing this piece - if it's using a full
                // PolygonCollider2D (the most expensive 2D collision shape
                // Unity has), swap it for a cheap CircleCollider2D sized to
                // match. Debris is disposable rubble, not something that
                // needs pixel-accurate hit detection the way a weapon's
                // weak spot does, so a rough approximation is a safe and
                // meaningful CPU saving with no visible downside.
                SimplifyColliderIfPolygon(d.gameObject);
            }

            while (tracked.Count > 0 && tracked.Peek() == null)
            {
                tracked.Dequeue();
            }

            // Real GPU load reduction: while a piece of debris is
            // off-screen, there's no reason to pay its draw call at all -
            // disable the renderer entirely rather than just leaving it
            // rendering into empty space outside the camera. The instant
            // it's back in view, the renderer flips back on with zero
            // visible pop since nothing about its transform/physics
            // changed while hidden.
            //
            // Collider/rigidbody sleeping: if a piece is BOTH off-screen
            // AND has settled to a near-standstill, manually put its
            // Rigidbody2D to sleep right now instead of waiting out
            // Unity's own timeToSleep countdown. This only ever touches
            // debris we're already tracking here - never a contraption's
            // welds, wires, or anything gameplay-relevant, so there's no
            // repeat of the stretchy-joint issue from touching the global
            // sleep threshold earlier this session.
            //
            // Visibility is computed once per item in this same pass and
            // reused below for the off-screen-first cleanup priority too,
            // so we're not checking the same thing twice per debris piece
            // per cycle.
            List<DebrisComponent> pool = new List<DebrisComponent>(tracked);
            Dictionary<DebrisComponent, bool> visibility = new Dictionary<DebrisComponent, bool>(pool.Count);

            foreach (DebrisComponent d in pool)
            {
                bool visible = IsVisible(d, out Renderer r, out Rigidbody2D rb);
                visibility[d] = visible;

                if (r != null && r.enabled != visible)
                {
                    r.enabled = visible;
                }

                if (!visible && rb != null && !rb.IsSleeping()
                    && rb.velocity.sqrMagnitude < SleepVelocityThreshold * SleepVelocityThreshold)
                {
                    rb.Sleep();
                }
            }

            int liveCount = tracked.Count;
            int activeCap = Mathf.RoundToInt(Mathf.Lerp(MaxDebrisPieces, MaxDebrisPiecesStrained, AutoOptimizer.StrainLevel));
            if (liveCount <= activeCap)
            {
                return;
            }

            int excess = liveCount - activeCap;

            // Culling method: off-screen-first cleanup priority. Rather
            // than strictly removing the oldest piece regardless of
            // visibility, prefer removing pieces that are currently
            // off-screen - nobody's watching those anyway, so a piece of
            // rubble that's been sitting in view stays around longer than
            // an older piece that's currently out of frame. Falls back to
            // oldest-first among visible pieces if everything queued is
            // actually on-screen.
            pool.Sort((a, b) =>
            {
                bool aVisible = visibility[a];
                bool bVisible = visibility[b];
                if (aVisible != bVisible)
                {
                    return aVisible ? 1 : -1; // off-screen sorts first (removed first)
                }
                return 0; // keep original (oldest-first) relative order otherwise
            });

            for (int i = 0; i < excess && i < pool.Count; i++)
            {
                DebrisComponent toRemove = pool[i];
                if (toRemove != null)
                {
                    trackedSet.Remove(toRemove);
                    SafeDestroy.Do(toRemove.gameObject);
                }
            }

            // Rebuild the queue without the pieces we just removed.
            tracked.Clear();
            foreach (DebrisComponent d in pool)
            {
                if (d != null && trackedSet.Contains(d))
                {
                    tracked.Enqueue(d);
                }
            }
        }

        private static bool IsVisible(DebrisComponent d, out Renderer renderer, out Rigidbody2D rigidbody)
        {
            renderer = null;
            rigidbody = null;

            if (d == null)
            {
                return false;
            }

            renderer = d.GetComponent<Renderer>();
            rigidbody = d.GetComponent<Rigidbody2D>();

            return renderer != null && renderer.isVisible;
        }

        // Swaps an expensive PolygonCollider2D for a cheap BoxCollider2D
        // sized to roughly match, only on debris large enough for it to
        // matter. Rigidbody2D is untouched so physics behavior (mass,
        // drag, gravity) stays identical - only the collision SHAPE gets
        // simplified. The box is sized to the polygon's bounds so it still
        // collides in roughly the right place, it's just not pixel-perfect
        // anymore, which is fine for rubble.
        // BUG FIX: originally used CircleCollider2D, but a perfect circle
        // has no flat resting face, so debris kept rolling away instead of
        // settling like the original chunky shape would have. A box has
        // flat edges to actually rest on and is nearly as cheap.
        // BUG FIX: a bounding box around a small, jagged, irregular shape
        // can be noticeably bigger than the actual visible sprite, and
        // that gap becomes a much bigger percentage of the object's own
        // size the smaller it is - this made tiny debris visibly hover
        // above surfaces instead of resting flush. Small polygons are
        // already cheap to simulate as-is (few vertices), so there's
        // barely any performance upside to touching them anyway - only
        // simplifying debris above a minimum size avoids the mismatch
        // entirely for the pieces where it would actually be noticeable.
        private const float MinSizeToSimplify = 0.4f;

        private static void SimplifyColliderIfPolygon(GameObject go)
        {
            PolygonCollider2D poly = go.GetComponent<PolygonCollider2D>();
            if (poly == null)
            {
                return;
            }

            Bounds bounds = poly.bounds;
            Vector2 size = bounds.size;

            if (Mathf.Max(size.x, size.y) < MinSizeToSimplify)
            {
                return;
            }

            Vector2 localCenter = poly.offset;

            bool wasTrigger = poly.isTrigger;
            PhysicsMaterial2D material = poly.sharedMaterial;

            // Object.Destroy() defers actual removal to the end of the
            // frame, so the old and new colliders could both technically
            // be active on the same object for one physics step -
            // DestroyImmediate guarantees the old one is truly gone first.
            // Safe here since this runs in a normal Update(), not inside a
            // physics callback.
            Object.DestroyImmediate(poly);

            BoxCollider2D box = go.AddComponent<BoxCollider2D>();
            box.offset = localCenter;
            box.size = size;
            box.isTrigger = wasTrigger;
            box.sharedMaterial = material;
        }
    }

    // Companion to the death hook above. Fully event-driven - unlike
    // DebrisLimiter this needs zero FindObjectsOfType scans at all, since
    // ModAPI.OnDeath tells us exactly when and which body died. Corpses
    // are tracked in true death order and the oldest ones get removed
    // first once the cap is crossed, same as debris settling/disappearing
    // over time. A generous cap by default (60) since removing bodies is
    // more noticeable than removing rubble - this is about preventing
    // unbounded pileup over a long session, not aggressive cleanup.
    public static class CorpseLimiter
    {
        private const int MaxCorpses = 60;
        private const int MaxCorpsesStrained = 30; // tighter cap when Auto Optimizer detects strain

        private static readonly Queue<PersonBehaviour> tracked = new Queue<PersonBehaviour>();

        public static void Track(PersonBehaviour person)
        {
            if (person == null)
            {
                return;
            }

            tracked.Enqueue(person);

            while (tracked.Count > 0 && tracked.Peek() == null)
            {
                tracked.Dequeue();
            }

            int activeCap = Mathf.RoundToInt(Mathf.Lerp(MaxCorpses, MaxCorpsesStrained, AutoOptimizer.StrainLevel));
            if (tracked.Count <= activeCap)
            {
                return;
            }

            int excess = tracked.Count - activeCap;
            for (int i = 0; i < excess && tracked.Count > 0; i++)
            {
                PersonBehaviour oldest = tracked.Dequeue();
                if (oldest != null)
                {
                    SafeDestroy.Do(oldest.gameObject);
                }
            }
        }
    }

    // Decals (blood, scorch marks, bullet impacts) never get cleaned up by
    // the base game - every single one is a permanent GameObject with its
    // own SpriteRenderer parented to whatever surface it landed on. Over
    // long combat sessions with explosions and incendiary rounds, this
    // pile keeps growing the whole time you play. This periodically trims
    // the oldest decals off any surface once it gets too cluttered, the
    // same way it would naturally fade away in a real environment.
    public class DecalLimiter : MonoBehaviour
    {
        private const int MaxDecalsPerSurface = 150;
        private const int MaxDecalsPerSurfaceStrained = 75; // tighter cap when Auto Optimizer detects strain
        private const float CheckInterval = 8f;

        private float timer;

        private void Update()
        {
            timer += Time.deltaTime;
            if (timer < CheckInterval)
            {
                return;
            }
            timer = 0f;

            DecalControllerBehaviour[] controllers = Object.FindObjectsOfType<DecalControllerBehaviour>();
            foreach (DecalControllerBehaviour dc in controllers)
            {
                if (dc == null || dc.decalHolder == null)
                {
                    continue;
                }

                Transform holder = dc.decalHolder.transform;
                int childCount = holder.childCount;
                int activeCap = Mathf.RoundToInt(Mathf.Lerp(MaxDecalsPerSurface, MaxDecalsPerSurfaceStrained, AutoOptimizer.StrainLevel));
                if (childCount <= activeCap)
                {
                    continue;
                }

                int excess = childCount - activeCap;

                // Culling method: off-screen-first cleanup priority, same
                // reasoning as debris. Prefer removing decals nobody's
                // currently looking at over strictly oldest-first, so a
                // fresh blood splatter right in view outlasts an older one
                // that's currently off-screen.
                //
                // Visibility is precomputed ONCE per child here instead of
                // inside the sort comparison - GetComponentInChildren is a
                // recursive search and noticeably more expensive than a
                // plain GetComponent, so avoiding repeated lookups during
                // Sort()'s O(n log n) comparisons matters more here than
                // it did for debris.
                bool[] visArray = new bool[childCount];
                for (int i = 0; i < childCount; i++)
                {
                    visArray[i] = IsDecalVisible(holder.GetChild(i));
                }

                List<int> indices = new List<int>(childCount);
                for (int i = 0; i < childCount; i++)
                {
                    indices.Add(i);
                }

                indices.Sort((a, b) =>
                {
                    bool aVisible = visArray[a];
                    bool bVisible = visArray[b];
                    if (aVisible != bVisible)
                    {
                        return aVisible ? 1 : -1; // off-screen sorts first (removed first)
                    }
                    return a.CompareTo(b); // oldest-first among same-visibility group
                });

                List<int> toRemove = indices.GetRange(0, Mathf.Min(excess, indices.Count));
                toRemove.Sort();
                toRemove.Reverse(); // remove highest index first so earlier indices stay valid

                foreach (int idx in toRemove)
                {
                    Transform child = holder.GetChild(idx);
                    if (child != null)
                    {
                        SafeDestroy.Do(child.gameObject);
                    }
                    if (idx < dc.localDecalPositions.Count)
                    {
                        dc.localDecalPositions.RemoveAt(idx);
                    }
                }
            }
        }

        // Defaults to "visible" when uncertain (no renderer found), so we
        // never mistakenly cull something that might actually be in view -
        // the safe direction to be wrong in.
        private static bool IsDecalVisible(Transform t)
        {
            if (t == null)
            {
                return true;
            }
            Renderer r = t.GetComponentInChildren<Renderer>();
            return r == null || r.isVisible;
        }
    }

    // Waits for the game's object pools (tracers, muzzle flashes, etc.) to
    // exist, then raises their max size once. This is a one-time runtime
    // field change, not a save-breaking or persistent modification - it
    // resets every time the game restarts since pools are recreated fresh.
    public class PoolBooster : MonoBehaviour
    {
        private const uint Multiplier = 6;
        private const uint Cap = 3000;

        private bool done;
        private int attempts;
        private const int MaxAttempts = 600; // roughly 10 seconds at 60fps

        private void Update()
        {
            if (done)
            {
                return;
            }

            attempts++;

            ObjectPoolBehaviour[] pools = Object.FindObjectsOfType<ObjectPoolBehaviour>();
            if (pools.Length > 0)
            {
                foreach (ObjectPoolBehaviour pool in pools)
                {
                    uint boosted = pool.MaxPoolSize * Multiplier;
                    pool.MaxPoolSize = (boosted > Cap) ? Cap : boosted;
                }
                done = true;
                Destroy(this);
                return;
            }

            if (attempts > MaxAttempts)
            {
                done = true;
                Destroy(this);
            }
        }
    }

    // Keeps a projectile on cheap Discrete collision detection while
    // nothing is nearby, and only upgrades to expensive Continuous
    // detection when something hittable is actually close. Range,
    // damage, and speed are never touched.
    public class SmartBulletCollision : MonoBehaviour
    {
        // Cached once and shared by every bullet, instead of every single
        // bullet doing its own string-based LayerMask.GetMask lookup in
        // Awake(). Small per-call cost, but with AA arrays firing hundreds
        // of rounds it adds up to a lot of repeated identical lookups for
        // zero benefit.
        private static int cachedMask = -1;
        private static int CachedMask
        {
            get
            {
                if (cachedMask == -1)
                {
                    cachedMask = LayerMask.GetMask("Objects", "Bounds");
                }
                return cachedMask;
            }
        }

        private Rigidbody2D rb;

        private const float CheckInterval = 0.08f; // ~12 checks/sec per bullet
        private const float CheckRadius = 3f;       // units around the bullet to scan

        private float timer;

        private void Awake()
        {
            rb = GetComponent<Rigidbody2D>();

            if (rb != null)
            {
                rb.collisionDetectionMode = CollisionDetectionMode2D.Discrete;
            }

            // Stagger timers across bullets so they don't all run their
            // OverlapCircle check on the exact same physics frame.
            timer = Random.Range(0f, CheckInterval);
        }

        private void FixedUpdate()
        {
            if (rb == null)
            {
                return;
            }

            timer += Time.fixedDeltaTime;
            if (timer < CheckInterval)
            {
                return;
            }
            timer = 0f;

            Collider2D nearby = Physics2D.OverlapCircle(rb.position, CheckRadius, CachedMask);

            rb.collisionDetectionMode = (nearby != null)
                ? CollisionDetectionMode2D.Continuous
                : CollisionDetectionMode2D.Discrete;
        }
    }

    // Companion attached to every bleeding wound's particle effect. Checks
    // roughly 5 times a second whether the effect is actually visible to
    // the camera right now, and only enables/disables the expensive
    // BleedingParticleBehaviour based on that. Visible wounds are always
    // fully live - this only ever pauses work nobody could see anyway.
    public class BleedingParticleCuller : MonoBehaviour
    {
        private const float CheckInterval = 0.2f;

        private BleedingParticleBehaviour target;
        private Renderer targetRenderer;
        private float timer;

        private void Awake()
        {
            target = GetComponent<BleedingParticleBehaviour>();
            targetRenderer = GetComponent<Renderer>();

            // Stagger checks across many simultaneous wounds so they don't
            // all evaluate on the exact same frame.
            timer = Random.Range(0f, CheckInterval);
        }

        private void Update()
        {
            if (target == null || targetRenderer == null)
            {
                return;
            }

            timer += Time.deltaTime;
            if (timer < CheckInterval)
            {
                return;
            }
            timer = 0f;

            bool visible = targetRenderer.isVisible;

            if (target.enabled != visible)
            {
                target.enabled = visible;
            }
        }
    }

    // EXPERIMENTAL. Extends the same "put idle things to sleep early"
    // logic from DebrisLimiter to EVERY rigidbody in the scene, including
    // jointed/welded contraption parts this time - previously we
    // deliberately excluded anything with a joint, specifically to avoid
    // repeating the Red Sky stretchy-weld issue. This is a direct test of
    // the theory that the stretchiness is actually inherent to how Red
    // Sky's own "Rigid" wires are built (DistanceJoint2D trusses under
    // heavy turret mass needing full solver convergence), not something
    // caused by sleep timing - if that's right, this should be safe even
    // on jointed structures. If it's wrong, revert is one line in Main().
    //
    // Requires SEVERAL consecutive checks of near-zero velocity (not just
    // one instant reading) before sleeping anything, specifically because
    // a heavy truss still actively converging under load could briefly
    // show near-zero velocity between solver steps without actually being
    // settled yet - a single low reading isn't enough evidence on its own.
    public class IdleRigidbodySleeper : MonoBehaviour
    {
        private const float ScanInterval = 0.5f;
        private const float VelocityThreshold = 0.03f;
        private const int RequiredConsecutiveIdleChecks = 4; // ~2 seconds of sustained stillness

        private readonly Dictionary<Rigidbody2D, int> idleStreaks = new Dictionary<Rigidbody2D, int>();
        private float timer;

        private void Update()
        {
            timer += Time.deltaTime;
            if (timer < ScanInterval)
            {
                return;
            }
            timer = 0f;

            Rigidbody2D[] allBodies = Object.FindObjectsOfType<Rigidbody2D>();
            HashSet<Rigidbody2D> stillPresent = new HashSet<Rigidbody2D>();

            foreach (Rigidbody2D rb in allBodies)
            {
                if (rb == null || rb.IsSleeping() || rb.bodyType != RigidbodyType2D.Dynamic)
                {
                    continue;
                }

                // BUG FIX: excluding anything belonging to a live human.
                // Ragdoll limbs are joint-dense and delicate - waking a
                // slept limb right as it takes an impact can produce a
                // much harsher "cold" collision response than a body
                // that was continuously simulated the whole time, which
                // showed up as humans crushing/breaking bones far too
                // easily. Corpses are handled separately by CorpseLimiter
                // anyway, so nothing about human ragdolls needs this.
                if (rb.GetComponentInParent<PersonBehaviour>() != null)
                {
                    continue;
                }

                stillPresent.Add(rb);

                bool nearlyStill = rb.velocity.sqrMagnitude < VelocityThreshold * VelocityThreshold
                    && Mathf.Abs(rb.angularVelocity) < VelocityThreshold * 10f;

                if (!nearlyStill)
                {
                    idleStreaks[rb] = 0;
                    continue;
                }

                int streak = idleStreaks.TryGetValue(rb, out int s) ? s + 1 : 1;
                idleStreaks[rb] = streak;

                if (streak >= RequiredConsecutiveIdleChecks)
                {
                    rb.Sleep();
                }
            }

            // Clean up tracking entries for bodies that got destroyed or
            // went to sleep on their own since the last scan, so this
            // dictionary doesn't grow unbounded over a long session.
            if (idleStreaks.Count > stillPresent.Count)
            {
                List<Rigidbody2D> stale = null;
                foreach (Rigidbody2D rb in idleStreaks.Keys)
                {
                    if (!stillPresent.Contains(rb))
                    {
                        (stale ??= new List<Rigidbody2D>()).Add(rb);
                    }
                }
                if (stale != null)
                {
                    foreach (Rigidbody2D rb in stale)
                    {
                        idleStreaks.Remove(rb);
                    }
                }
            }
        }
    }

    // Auto-adjusts Physics2D.velocityIterations along the same shared
    // "Auto Optimizer" strain curve as debris/decals/corpses, instead of its
    // own separate stepped logic. Deliberately does NOT touch
    // positionIterations - that's the one that caused visibly stretchy/
    // bouncy welds on heavy contraptions (Red Sky) when we tried lowering
    // it earlier this session. Velocity iterations mainly affect how
    // quickly relative velocities converge (things like how "springy"
    // fast-moving free bodies feel settling down), which is a lot less
    // likely to cause visible joint softness on welded builds.
    //
    // Scales in BOTH directions now. Below AutoOptimizer's relaxed
    // threshold, reads StrainLevel and lerps down toward a safe floor,
    // same as before. Above that same threshold, reads SmoothedFps
    // directly and scales UP toward a higher ceiling as performance heads
    // into genuinely excellent territory (120+ fps), capped at 128. This
    // is a different category from the visual settings lock: physics
    // iteration count isn't an aesthetic choice the way Bloom is, it's a
    // pure accuracy/performance dial - more accuracy when there's
    // headroom to spare is one-directionally desirable, nobody's upset
    // their joints got MORE stable. Never scales up past whatever the
    // player's own baseline already was if that baseline is already
    // above the ceiling.
    public class AutoVelocityIterationTuner : MonoBehaviour
    {
        private const int MinIterations = 5;   // never go lower than this, even at MAX STRAIN
        private const int MaxIterations = 128; // ceiling for the upward scaling, reached at UpscaleCeilingFps
        private const float UpscaleCeilingFps = 120f; // fps at which iterations hit MaxIterations

        private int defaultIterations;

        private void Start()
        {
            defaultIterations = Physics2D.velocityIterations;
        }

        private void Update()
        {
            int target;

            if (AutoOptimizer.StrainLevel > 0f)
            {
                target = Mathf.RoundToInt(Mathf.Lerp(defaultIterations, MinIterations, AutoOptimizer.StrainLevel));
            }
            else
            {
                // Never lerps below defaultIterations even if someone's
                // own baseline preference already exceeds MaxIterations -
                // this branch only ever increases, never decreases.
                float ceiling = Mathf.Max(defaultIterations, MaxIterations);
                float t = Mathf.InverseLerp(50f, UpscaleCeilingFps, AutoOptimizer.SmoothedFps);
                target = Mathf.RoundToInt(Mathf.Lerp(defaultIterations, ceiling, t));
            }

            if (Physics2D.velocityIterations != target)
            {
                Physics2D.velocityIterations = target;
            }
        }
    }

    // Ties the game's own built-in visual performance settings (Bloom,
    // Bullet tracers, Fancy effects, Decals) into the same "Auto Optimizer"
    // strain curve everything else uses. Unlike the earlier caps, these
    // are genuinely visible/gameplay-facing settings, so they're escalated
    // in order from least to most disruptive as strain climbs, and only
    // the last one (bullet tracers, which controls whether bullets have
    // real travel time - a real gameplay feel change) kicks in at true
    // MAX STRAIN as an absolute last resort. Lighting is intentionally
    // never touched since the game itself notes it barely affects
    // performance, so there's no reason to change how the game looks for
    // no real gain.
    //
    // Because these are plain fields (not properties we could intercept),
    // "locking" them works by capturing whatever the user had set the
    // moment the mod loads, then continuously enforcing the Auto-Optimizer-
    // decided value the whole time the mod is active - any manual change
    // in the settings menu gets reverted within about half a second. This
    // is what makes the values effectively mod-controlled while active,
    // as requested.
    public class AutoOptimizerVisualLock : MonoBehaviour
    {
        private const float CheckInterval = 0.5f;

        // Strain thresholds, escalating from least to most disruptive.
        // BUG FIX: Bloom used to be first in line at 0.25, but neon/glow
        // effects depend entirely on it - going colorless too easily was
        // a bigger visible hit than the modest performance gain justified.
        // Fancy Effects goes first now instead, Bloom pushed back later.
        //
        // Distant Sound Effects added after finding the game's own tooltip
        // confirms it: "Disabling this setting makes audio processing a
        // bit less demanding." A real, sanctioned audio cost with zero
        // visual impact, so it goes first in line - even less disruptive
        // than Fancy Effects.
        private const float DistantSoundOffThreshold = 0.15f;
        private const float FancyEffectsOffThreshold = 0.25f;
        private const float BloomOffThreshold = 0.55f;
        private const float RenderScaleThreshold = 0.6f; // starts softening resolution here
        private const float DecalsOffThreshold = 0.75f;
        private const float TracersOffThreshold = 0.95f; // last resort only, near true MAX STRAIN

        private const float MinRenderScale = 0.75f; // never goes below this even at MAX STRAIN - still readable, just softer

        private BloomMode baselineBloom;
        private bool baselineFancyEffects;
        private bool baselineDecals;
        private bool baselineTracers;
        private bool baselineDistantSound;
        private float baselineRenderScale;

        private float timer;

        // A previous version of this mod could permanently corrupt these
        // exact 4 settings for someone if they quit while strain was
        // elevated (fixed above via OnApplicationQuit/OnDestroy, but that
        // only prevents FUTURE corruption). Anyone already affected has
        // these degraded values baked into their real config.json, and
        // this mod has no way to know what they actually had before the
        // bug hit them - there's no backup, no history. Rather than guess,
        // this checks once on first load after updating whether things
        // look suspiciously degraded compared to the game's real defaults,
        // and if so, offers a one-time choice instead of silently
        // overwriting anything: reset to the game's actual defaults, or
        // keep things exactly as they currently are.
        private const string RecoveryPromptShownKey = "PPGPerformanceSuite_RecoveryPromptShown";

        private void Start()
        {
            Preferences prefs = UserPreferenceManager.Current;
            baselineBloom = prefs.BloomMode;
            baselineFancyEffects = prefs.FancyEffects;
            baselineDecals = prefs.Decals;
            baselineTracers = prefs.TracerBullets;
            baselineDistantSound = prefs.DistantSoundEffects;
            baselineRenderScale = prefs.RenderScale;

            if (PlayerPrefs.GetInt(RecoveryPromptShownKey, 0) == 0)
            {
                bool looksAffected = baselineBloom == BloomMode.Off
                    || !baselineFancyEffects
                    || !baselineDecals
                    || baselineRenderScale < 1f;

                if (looksAffected)
                {
                    StartCoroutine(ShowRecoveryPromptWhenReady());
                }
                else
                {
                    PlayerPrefs.SetInt(RecoveryPromptShownKey, 1);
                    PlayerPrefs.Save();
                }
            }
        }

        private System.Collections.IEnumerator ShowRecoveryPromptWhenReady()
        {
            float waited = 0f;
            while (DialogBoxManager.Main == null && waited < 10f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            PlayerPrefs.SetInt(RecoveryPromptShownKey, 1);
            PlayerPrefs.Save();

            if (DialogBoxManager.Main == null)
            {
                yield break;
            }

            string message =
                "A few of your video settings (Bloom, Fancy Effects, Decals, " +
                "or Render Scale) look lower than the game's defaults.\n\n" +
                "An older version of this mod had a bug that could permanently " +
                "lower these if the game closed at the wrong moment. That's " +
                "since been fixed, but this mod can't tell whether your current " +
                "values are from that old bug or just how you actually like " +
                "things.\n\n" +
                "Want to reset these 4 settings to the game's defaults? " +
                "Nothing will be changed unless you choose to.";

            DialogBox dialog = DialogBoxManager.Dialog(
                message,
                new DialogButton("Keep My Current Settings", true),
                new DialogButton("Reset to Defaults", true, () =>
                {
                    Preferences p = UserPreferenceManager.Current;
                    p.BloomMode = BloomMode.Fancy;
                    p.FancyEffects = true;
                    p.Decals = true;
                    p.RenderScale = 1f;

                    baselineBloom = p.BloomMode;
                    baselineFancyEffects = p.FancyEffects;
                    baselineDecals = p.Decals;
                    baselineRenderScale = p.RenderScale;
                })
            );

            if (dialog != null)
            {
                UnityEngine.UI.ContentSizeFitter fitter = dialog.TextMesh.GetComponent<UnityEngine.UI.ContentSizeFitter>();
                if (fitter != null)
                {
                    fitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                }

                UnityEngine.UI.ContentSizeFitter parentFitter = dialog.GetComponentInParent<UnityEngine.UI.ContentSizeFitter>();
                if (parentFitter != null)
                {
                    parentFitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                }

                RectTransform textRect = dialog.TextMesh.rectTransform;
                textRect.sizeDelta = new Vector2(650f, textRect.sizeDelta.y);
                dialog.TextMesh.enableWordWrapping = true;

                dialog.SetWidth(700f);
            }
        }

        // CRITICAL BUG FIX: PP saves these settings to a real config.json
        // on disk, and calls Save() from several places including on
        // quit. Without this, whatever strain had temporarily set (Bloom
        // off, lowered Render Scale, etc.) at the exact moment someone
        // closed the game would get written as their new PERMANENT
        // settings, with no way for them to know what their original
        // values even were. This forces every value back to the real
        // baseline immediately whenever the game is quitting or this
        // component is going away for any reason, so whatever gets saved
        // to disk is always the person's actual chosen settings, never a
        // temporary strain-driven one.
        private void OnApplicationQuit()
        {
            RestoreBaseline();
        }

        private void OnDestroy()
        {
            RestoreBaseline();
        }

        private void RestoreBaseline()
        {
            Preferences prefs = UserPreferenceManager.Current;
            if (prefs == null)
            {
                return;
            }

            prefs.BloomMode = baselineBloom;
            prefs.FancyEffects = baselineFancyEffects;
            prefs.Decals = baselineDecals;
            prefs.TracerBullets = baselineTracers;
            prefs.DistantSoundEffects = baselineDistantSound;
            prefs.RenderScale = baselineRenderScale;
        }

        private void Update()
        {
            timer += Time.unscaledDeltaTime;
            if (timer < CheckInterval)
            {
                return;
            }
            timer = 0f;

            Preferences prefs = UserPreferenceManager.Current;
            float strain = AutoOptimizer.StrainLevel;

            BloomMode desiredBloom = (strain >= BloomOffThreshold) ? BloomMode.Off : baselineBloom;
            bool desiredFancyEffects = (strain >= FancyEffectsOffThreshold) ? false : baselineFancyEffects;
            bool desiredDecals = (strain >= DecalsOffThreshold) ? false : baselineDecals;
            bool desiredTracers = (strain >= TracersOffThreshold) ? false : baselineTracers;
            bool desiredDistantSound = (strain >= DistantSoundOffThreshold) ? false : baselineDistantSound;

            // Between RenderScaleThreshold and DecalsOffThreshold, smoothly
            // scale resolution down from the user's own baseline toward
            // MinRenderScale rather than snapping - a gradually softening
            // image reads as far less jarring than an instant resolution
            // jump. Below the threshold it stays at exactly the user's
            // own chosen value, untouched.
            float desiredRenderScale = baselineRenderScale;
            if (strain >= RenderScaleThreshold)
            {
                float t = Mathf.InverseLerp(RenderScaleThreshold, DecalsOffThreshold, strain);
                float floor = Mathf.Min(baselineRenderScale, MinRenderScale);
                desiredRenderScale = Mathf.Lerp(baselineRenderScale, floor, t);
            }

            if (prefs.BloomMode != desiredBloom)
            {
                prefs.BloomMode = desiredBloom;
            }
            if (prefs.FancyEffects != desiredFancyEffects)
            {
                prefs.FancyEffects = desiredFancyEffects;
            }
            if (prefs.Decals != desiredDecals)
            {
                prefs.Decals = desiredDecals;
            }
            if (prefs.TracerBullets != desiredTracers)
            {
                prefs.TracerBullets = desiredTracers;
            }
            if (prefs.DistantSoundEffects != desiredDistantSound)
            {
                prefs.DistantSoundEffects = desiredDistantSound;
            }
            if (!Mathf.Approximately(prefs.RenderScale, desiredRenderScale))
            {
                prefs.RenderScale = desiredRenderScale;
            }
        }
    }

    // Shows a one-time explanatory popup the moment the mod loads, so
    // nobody is surprised when their visual settings start changing on
    // their own during heavy lag. Checks a PlayerPrefs flag first - Unity's
    // own standard mechanism for remembering small settings across game
    // restarts, completely separate from PP's own save files, so there's
    // zero risk of this touching or corrupting any actual game data. If
    // the person clicked "Never Show Again" previously, this flag is set
    // and the popup is skipped permanently (until they clear it, since
    // there's no in-mod way to reset it otherwise - that's an acceptable
    // tradeoff for something this low-stakes).
    public class WelcomePopup : MonoBehaviour
    {
        private const string HidePrefKey = "PPGPerformanceSuite_HideWelcomePopup";

        private void Start()
        {
            if (PlayerPrefs.GetInt(HidePrefKey, 0) == 1)
            {
                return;
            }

            StartCoroutine(ShowWhenReady());
        }

        private System.Collections.IEnumerator ShowWhenReady()
        {
            float waited = 0f;
            while (DialogBoxManager.Main == null && waited < 10f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (DialogBoxManager.Main == null)
            {
                yield break;
            }

            string message =
                "PPG Performance Suite is active.\n\n" +
                "Auto Optimizer watches your FPS and automatically eases in performance " +
                "adjustments only when needed, then relaxes them back once things " +
                "recover - nothing changes while your FPS is healthy.\n\n" +
                "Under heavy strain it will, in order: reduce Bloom, then Fancy " +
                "Effects, then gradually lower Render Scale, then Decals, and only " +
                "as an absolute last resort at very low FPS, disable Bullet " +
                "Tracers. Lighting is never touched.\n\n" +
                "While this mod is active, these visual settings are managed " +
                "automatically and can't be changed manually in the settings menu.\n\n" +
                "Other features: bigger effect pools (no more missing tracers " +
                "during heavy fire), automatic cleanup of piled-up debris/decals/" +
                "corpses, smarter bullet collision checks, and physics tuning - " +
                "all invisible until they're actually needed.";

            DialogBox dialog = DialogBoxManager.Dialog(
                message,
                new DialogButton("OK", true),
                new DialogButton("Never Show Again", true, () =>
                {
                    PlayerPrefs.SetInt(HidePrefKey, 1);
                    PlayerPrefs.Save();
                })
            );

            if (dialog != null)
            {
                // The outer box wasn't actually the problem - it likely has
                // a ContentSizeFitter auto-sizing itself to fit the text as
                // one unwrapped line, which overrides SetWidth on the outer
                // container. Force the fitter (if present) to stop
                // controlling width, then explicitly set a fixed width and
                // word wrap directly on the text element itself.
                UnityEngine.UI.ContentSizeFitter fitter = dialog.TextMesh.GetComponent<UnityEngine.UI.ContentSizeFitter>();
                if (fitter != null)
                {
                    fitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                }

                UnityEngine.UI.ContentSizeFitter parentFitter = dialog.GetComponentInParent<UnityEngine.UI.ContentSizeFitter>();
                if (parentFitter != null)
                {
                    parentFitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                }

                RectTransform textRect = dialog.TextMesh.rectTransform;
                textRect.sizeDelta = new Vector2(650f, textRect.sizeDelta.y);
                dialog.TextMesh.enableWordWrapping = true;

                dialog.SetWidth(700f);
            }
        }
    }

    // Companion attached to every fire's mesh component. Checks roughly 5
    // times a second whether the fire is actually visible to the camera
    // right now, and only enables/disables the expensive FireMeshBehaviour
    // (which regenerates and re-uploads mesh geometry every frame) based on
    // that. Visible fires are always fully live and animated - this only
    // ever pauses the mesh flicker work nobody could see anyway. Burn
    // damage/spread logic lives elsewhere and is untouched.
    public class FireMeshCuller : MonoBehaviour
    {
        private const float CheckInterval = 0.2f;

        private FireMeshBehaviour target;
        private Renderer targetRenderer;
        private float timer;

        private void Awake()
        {
            target = GetComponent<FireMeshBehaviour>();
            targetRenderer = GetComponent<Renderer>();

            timer = Random.Range(0f, CheckInterval);
        }

        private void Update()
        {
            if (target == null || targetRenderer == null)
            {
                return;
            }

            timer += Time.deltaTime;
            if (timer < CheckInterval)
            {
                return;
            }
            timer = 0f;

            bool visible = targetRenderer.isVisible;

            if (target.enabled != visible)
            {
                target.enabled = visible;
            }
        }
    }

    // Audio in PP has zero central management - every gun, car, and machine
    // just calls AudioSource.Play() directly whenever it wants, with no
    // pooling or cap of any kind. Unlike the visual/physics stuff in this
    // suite, muting sound is a genuinely different category of change:
    // off-screen sound often still matters to the player (a gunfight just
    // out of frame, an explosion nearby) as real gameplay awareness, not
    // just visual clutter nobody's looking at. So this is deliberately
    // built as a rare, last-resort safety net rather than routine cleanup:
    // it only ever mutes something if BOTH of these are true at once -
    // there are already an extreme number of sounds playing simultaneously
    // (well beyond anything normal play would ever produce) AND that
    // specific sound's source is also off-screen. Under any normal amount
    // of simultaneous audio, or for anything currently visible, nothing
    // here does anything at all. Mutes (volume - reversible) rather than
    // stops, so nothing gets cut off or restarts oddly if conditions change
    // a moment later.
    public class AudioSafetyCap : MonoBehaviour
    {
        private const int SafetyCapCount = 100; // far beyond any normal scenario
        private const float ScanInterval = 5f; // this is a rare emergency safety net, not routine cleanup - doesn't need per-second scanning

        private float timer;
        private readonly List<AudioSource> mutedByUs = new List<AudioSource>();

        private void Update()
        {
            timer += Time.unscaledDeltaTime;
            if (timer < ScanInterval)
            {
                return;
            }
            timer = 0f;

            // Restore anything we previously muted in case conditions
            // have changed (count dropped, or it's now visible).
            foreach (AudioSource restored in mutedByUs)
            {
                if (restored != null)
                {
                    restored.mute = false;
                }
            }
            mutedByUs.Clear();

            AudioSource[] allSources = Object.FindObjectsOfType<AudioSource>();
            List<AudioSource> playing = new List<AudioSource>();
            foreach (AudioSource src in allSources)
            {
                if (src != null && src.isPlaying)
                {
                    playing.Add(src);
                }
            }

            if (playing.Count <= SafetyCapCount)
            {
                return;
            }

            int excess = playing.Count - SafetyCapCount;
            foreach (AudioSource src in playing)
            {
                if (excess <= 0)
                {
                    break;
                }

                if (!IsAudioSourceVisible(src))
                {
                    src.mute = true;
                    mutedByUs.Add(src);
                    excess--;
                }
            }
        }

        private static bool IsAudioSourceVisible(AudioSource src)
        {
            Renderer r = src.GetComponentInParent<Renderer>();
            return r == null || r.isVisible;
        }
    }

    // The session-long "builds up over time" cache, keyed by SpawnableAsset
    // (the identifier PP gives us for what TYPE of thing just spawned, not
    // the specific instance). The first time a given asset type spawns, we
    // pay the full cost of checking all four systems and remember which
    // ones it structurally has. Every later spawn of that exact same asset
    // skips the searches already confirmed empty. Lives only in memory for
    // the current session - resets naturally on restart, never written to
    // any save file, so there's no possibility of it going stale in a way
    // that persists.
    public static class SpawnStructureCache
    {
        public struct ComponentPresence
        {
            public bool HasLaunchers;
            public bool HasCirculation;
            public bool HasBleeding;
            public bool HasFire;
        }

        private static readonly Dictionary<SpawnableAsset, ComponentPresence> cache = new Dictionary<SpawnableAsset, ComponentPresence>();

        public static bool TryGet(SpawnableAsset asset, out ComponentPresence presence)
        {
            if (asset == null)
            {
                presence = default;
                return false;
            }
            return cache.TryGetValue(asset, out presence);
        }

        public static void Record(SpawnableAsset asset, ComponentPresence presence)
        {
            if (asset != null)
            {
                cache[asset] = presence;
            }
        }
    }

    // Adds a small note directly onto the game's own Settings menu, right
    // where someone would actually try to change one of the 5 settings
    // Auto Optimizer manages - rather than relying only on the load-time
    // popup, which they may have already dismissed and forgotten about.
    // The settings menu is generated fresh from reflection over the
    // Preferences class (see SettingsGeneratorBehaviour), not from a
    // static prefab, so this waits for that generation to happen and then
    // appends to each target setting's existing description text. Runs on
    // a light ongoing recheck (not just once) so it self-heals if the menu
    // ever gets regenerated later in the session - never appends twice to
    // the same one.
    public class SettingsMenuNoteInjector : MonoBehaviour
    {
        private const float ScanInterval = 2f;
        private const string Note = " (Managed by Auto Optimizer)";

        private static readonly HashSet<string> TargetLabels = new HashSet<string>
        {
            "Bloom",
            "Bullet tracers",
            "Fancy effects",
            "Decals",
            "Render scale",
            "Distant sound effects"
        };

        private readonly HashSet<SettingTemplateBehaviour> alreadyPatched = new HashSet<SettingTemplateBehaviour>();
        private float timer;

        private void Update()
        {
            timer += Time.unscaledDeltaTime;
            if (timer < ScanInterval)
            {
                return;
            }
            timer = 0f;

            SettingTemplateBehaviour[] settings = Object.FindObjectsOfType<SettingTemplateBehaviour>(includeInactive: true);
            foreach (SettingTemplateBehaviour setting in settings)
            {
                if (setting == null || alreadyPatched.Contains(setting) || setting.Label == null)
                {
                    continue;
                }

                if (!TargetLabels.Contains(setting.Label.text))
                {
                    continue;
                }

                if (setting.Description != null && !setting.Description.text.Contains(Note))
                {
                    setting.Description.gameObject.SetActive(true);
                    setting.Description.text += Note;
                }

                alreadyPatched.Add(setting);
            }
        }
    }
}
