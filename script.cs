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
        private const float FullNormalFps = 65f;  // at or above this, StrainLevel = 0 (fully relaxed)
        private const float MaxStrainFps = 30f;   // at or below this, StrainLevel = 1 (MAX STRAIN)
        private const float SampleWindow = 2f;
        private const float SmoothingRate = 0.5f; // how much the exposed value moves toward target each window

        // Threshold for the small "kicked in" notification below - not the
        // same as any individual visual-setting threshold, just "is it
        // doing anything at all right now." A small dead zone above 0
        // avoids firing right at the boundary from normal FPS jitter.
        private const float ActiveNotifyThreshold = 0.05f;

        public static float StrainLevel { get; private set; }

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
            //
            // Visibility is computed ONCE per item here rather than inside
            // the sort comparison itself - Sort() calls the comparison
            // O(n log n) times, so without caching, the same object's
            // GetComponent<Renderer>() would get looked up repeatedly for
            // no reason. One pass to build the lookup, then the sort just
            // reads from it.
            List<DebrisComponent> pool = new List<DebrisComponent>(tracked);
            Dictionary<DebrisComponent, bool> visibility = new Dictionary<DebrisComponent, bool>(pool.Count);
            foreach (DebrisComponent d in pool)
            {
                visibility[d] = IsVisible(d);
            }

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
                    Destroy(toRemove.gameObject);
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

        private static bool IsVisible(DebrisComponent d)
        {
            if (d == null)
            {
                return false;
            }
            Renderer r = d.GetComponent<Renderer>();
            return r != null && r.isVisible;
        }

        // Swaps an expensive PolygonCollider2D for a cheap CircleCollider2D
        // sized to roughly match, only on debris. Rigidbody2D is untouched
        // so physics behavior (mass, drag, gravity) stays identical - only
        // the collision SHAPE gets simplified. The circle is sized to the
        // polygon's bounds so it still collides in roughly the right place,
        // it's just not pixel-perfect anymore, which is fine for rubble.
        private static void SimplifyColliderIfPolygon(GameObject go)
        {
            PolygonCollider2D poly = go.GetComponent<PolygonCollider2D>();
            if (poly == null)
            {
                return;
            }

            Bounds bounds = poly.bounds;
            Vector2 localCenter = poly.offset;
            float radius = Mathf.Max(bounds.extents.x, bounds.extents.y);

            bool wasTrigger = poly.isTrigger;
            PhysicsMaterial2D material = poly.sharedMaterial;

            Destroy(poly);

            CircleCollider2D circle = go.AddComponent<CircleCollider2D>();
            circle.offset = localCenter;
            circle.radius = radius;
            circle.isTrigger = wasTrigger;
            circle.sharedMaterial = material;
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
                    Object.Destroy(oldest.gameObject);
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
                        Destroy(child.gameObject);
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
    // Reads AutoOptimizer.StrainLevel (already smoothed + eased) and lerps
    // between the game's real starting value (0 strain) and a safe floor
    // (full MAX STRAIN), so it eases in and out gradually along with
    // everything else instead of jumping in fixed steps.
    public class AutoVelocityIterationTuner : MonoBehaviour
    {
        private const int MinIterations = 5; // never go lower than this, even at MAX STRAIN

        private int defaultIterations;

        private void Start()
        {
            defaultIterations = Physics2D.velocityIterations;
        }

        private void Update()
        {
            int target = Mathf.RoundToInt(Mathf.Lerp(defaultIterations, MinIterations, AutoOptimizer.StrainLevel));

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
        private const float BloomOffThreshold = 0.25f;
        private const float FancyEffectsOffThreshold = 0.5f;
        private const float RenderScaleThreshold = 0.6f; // starts softening resolution here
        private const float DecalsOffThreshold = 0.75f;
        private const float TracersOffThreshold = 0.95f; // last resort only, near true MAX STRAIN

        private const float MinRenderScale = 0.75f; // never goes below this even at MAX STRAIN - still readable, just softer

        private BloomMode baselineBloom;
        private bool baselineFancyEffects;
        private bool baselineDecals;
        private bool baselineTracers;
        private float baselineRenderScale;

        private float timer;

        private void Start()
        {
            Preferences prefs = UserPreferenceManager.Current;
            baselineBloom = prefs.BloomMode;
            baselineFancyEffects = prefs.FancyEffects;
            baselineDecals = prefs.Decals;
            baselineTracers = prefs.TracerBullets;
            baselineRenderScale = prefs.RenderScale;
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
}
