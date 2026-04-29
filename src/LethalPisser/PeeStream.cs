using System;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalPisser;

internal sealed class PeeStream : MonoBehaviour
{
    private const int MaxSegments = 16;
    private const float SegmentTime = 0.045f;
    private const float StreamSpeed = 11.5f;
    private const float GravityScale = 0.58f;
    private const float SplashEmissionRate = 45f;
    private const float BaseAudioVolume = 0.08f;
    private const float FizzleDuration = 0.7f;
    private const float MinimumShockStrength = 0.35f;

    private static readonly Color streamColor = new Color(1f, 0.78f, 0.12f, 0.92f);
    private static readonly Color streamEndColor = new Color(1f, 0.95f, 0.35f, 0.55f);
    private static Material? streamMaterial;
    private static AudioClip? streamAudioClip;

    private readonly Vector3[] pathPoints = new Vector3[MaxSegments];
    private readonly RaycastHit[] raycastHits = new RaycastHit[12];

    private PlayerControllerB? player;
    private Action<PlayerControllerB, ItemCharger, Vector3>? chargerHitHandler;
    private LineRenderer lineRenderer = null!;
    private ParticleSystem splashParticles = null!;
    private AudioSource audioSource = null!;
    private float seed;
    private bool isFlowing = true;
    private float fizzleTimer;

    public bool HasValidTarget => player != null
        && player.isPlayerControlled
        && !player.isPlayerDead
        && player.gameObject.activeInHierarchy;

    public static PeeStream Create(PlayerControllerB player, Action<PlayerControllerB, ItemCharger, Vector3> chargerHitHandler)
    {
        GameObject streamObject = new GameObject($"LethalPisser_Stream_{player.playerClientId}");
        PeeStream stream = streamObject.AddComponent<PeeStream>();
        stream.Bind(player, chargerHitHandler);
        return stream;
    }

    public void Bind(PlayerControllerB targetPlayer, Action<PlayerControllerB, ItemCharger, Vector3> chargerHitHandler)
    {
        player = targetPlayer;
        this.chargerHitHandler = chargerHitHandler;
        isFlowing = true;
        fizzleTimer = 0f;
    }

    public void StopFlow()
    {
        isFlowing = false;
        fizzleTimer = 0f;
    }

    private void Awake()
    {
        seed = UnityEngine.Random.Range(0f, 1000f);
        lineRenderer = gameObject.AddComponent<LineRenderer>();
        ConfigureLineRenderer();
        ConfigureSplashParticles();
        ConfigureAudio();
    }

    private void OnDestroy()
    {
        if (audioSource != null)
        {
            audioSource.Stop();
        }
    }

    private void LateUpdate()
    {
        if (!HasValidTarget || player == null)
        {
            Destroy(gameObject);
            return;
        }

        float streamStrength = GetStreamStrength();
        if (streamStrength <= 0f)
        {
            Destroy(gameObject);
            return;
        }

        if (!TryGetEmitter(streamStrength, out Vector3 origin, out Vector3 velocity))
        {
            return;
        }

        bool hitSurface = BuildPath(origin, velocity, streamStrength, out int pointCount, out Vector3 impactPoint, out Vector3 impactNormal, out Collider? hitCollider);
        lineRenderer.positionCount = pointCount;
        lineRenderer.widthMultiplier = streamStrength;

        for (int i = 0; i < pointCount; i++)
        {
            lineRenderer.SetPosition(i, pathPoints[i]);
        }

        splashParticles.transform.position = impactPoint + impactNormal * 0.015f;
        splashParticles.transform.rotation = Quaternion.LookRotation(impactNormal);
        SetSplashEmission(hitSurface ? SplashEmissionRate * streamStrength : 0f);

        audioSource.transform.position = origin;
        audioSource.volume = BaseAudioVolume * streamStrength;
        if (!audioSource.isPlaying)
        {
            audioSource.Play();
        }

        if (hitSurface && streamStrength >= MinimumShockStrength && hitCollider != null)
        {
            ItemCharger charger = hitCollider.GetComponentInParent<ItemCharger>();
            if (charger != null)
            {
                chargerHitHandler?.Invoke(player, charger, impactPoint);
            }
        }
    }

    private void ConfigureLineRenderer()
    {
        lineRenderer.useWorldSpace = true;
        lineRenderer.textureMode = LineTextureMode.Stretch;
        lineRenderer.alignment = LineAlignment.View;
        lineRenderer.numCapVertices = 5;
        lineRenderer.numCornerVertices = 3;
        lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lineRenderer.receiveShadows = false;
        lineRenderer.widthCurve = new AnimationCurve(
            new Keyframe(0f, 0.035f),
            new Keyframe(0.7f, 0.028f),
            new Keyframe(1f, 0.012f));

        Material? material = GetMaterial();
        if (material != null)
        {
            lineRenderer.material = material;
        }

        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(streamColor, 0f),
                new GradientColorKey(streamEndColor, 1f)
            },
            new[]
            {
                new GradientAlphaKey(streamColor.a, 0f),
                new GradientAlphaKey(streamEndColor.a, 1f)
            });
        lineRenderer.colorGradient = gradient;
    }

    private void ConfigureSplashParticles()
    {
        GameObject splashObject = new GameObject("Splash");
        splashObject.transform.SetParent(transform, worldPositionStays: false);

        splashParticles = splashObject.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = splashParticles.main;
        main.loop = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.12f, 0.28f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.85f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.018f, 0.04f);
        main.startColor = new ParticleSystem.MinMaxGradient(streamColor, streamEndColor);
        main.maxParticles = 90;

        ParticleSystem.EmissionModule emission = splashParticles.emission;
        emission.rateOverTime = SplashEmissionRate;

        ParticleSystem.ShapeModule shape = splashParticles.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 38f;
        shape.radius = 0.025f;

        ParticleSystem.VelocityOverLifetimeModule velocity = splashParticles.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.y = new ParticleSystem.MinMaxCurve(0.08f, 0.45f);
        velocity.x = new ParticleSystem.MinMaxCurve(-0.25f, 0.25f);
        velocity.z = new ParticleSystem.MinMaxCurve(-0.25f, 0.25f);

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = splashParticles.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient fade = new Gradient();
        fade.SetKeys(
            new[]
            {
                new GradientColorKey(streamColor, 0f),
                new GradientColorKey(streamEndColor, 1f)
            },
            new[]
            {
                new GradientAlphaKey(0.85f, 0f),
                new GradientAlphaKey(0f, 1f)
            });
        colorOverLifetime.color = fade;

        ParticleSystemRenderer renderer = splashObject.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortingOrder = 1;

        Material? material = GetMaterial();
        if (material != null)
        {
            renderer.material = material;
        }
    }

    private void ConfigureAudio()
    {
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.clip = GetAudioClip();
        audioSource.loop = true;
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 1f;
        audioSource.dopplerLevel = 0f;
        audioSource.rolloffMode = AudioRolloffMode.Linear;
        audioSource.minDistance = 1.5f;
        audioSource.maxDistance = 9f;
        audioSource.volume = BaseAudioVolume;
    }

    private float GetStreamStrength()
    {
        if (isFlowing)
        {
            return 1f;
        }

        fizzleTimer += Time.deltaTime;
        float normalizedTime = Mathf.Clamp01(fizzleTimer / FizzleDuration);
        return Mathf.SmoothStep(1f, 0f, normalizedTime);
    }

    private bool TryGetEmitter(float streamStrength, out Vector3 origin, out Vector3 velocity)
    {
        origin = Vector3.zero;
        velocity = Vector3.zero;

        if (player == null)
        {
            return false;
        }

        Transform aimTransform = player.gameplayCamera != null ? player.gameplayCamera.transform : player.transform;
        Vector3 forward = aimTransform.forward;
        Vector3 playerForward = player.transform.forward;
        Vector3 right = Vector3.Cross(Vector3.up, playerForward);
        if (right.sqrMagnitude < 0.01f)
        {
            right = player.transform.right;
        }

        right.Normalize();

        bool localPlayer = GameNetworkManager.Instance != null && GameNetworkManager.Instance.localPlayerController == player;
        origin = localPlayer
            ? aimTransform.position + forward * 0.22f - aimTransform.up * 0.48f + right * 0.04f
            : player.transform.position + Vector3.up * 0.78f + playerForward * 0.34f + right * 0.04f;

        float horizontalWiggle = Mathf.Sin(Time.time * 24f + seed) * 1.35f;
        float verticalWiggle = Mathf.Sin(Time.time * 31f + seed * 0.37f) * 0.75f;
        Vector3 direction = Quaternion.AngleAxis(horizontalWiggle, Vector3.up)
            * Quaternion.AngleAxis(verticalWiggle, right)
            * (forward + Vector3.down * 0.11f).normalized;

        velocity = direction.normalized * Mathf.Lerp(2.5f, StreamSpeed, streamStrength);
        return true;
    }

    private bool BuildPath(Vector3 origin, Vector3 velocity, float streamStrength, out int pointCount, out Vector3 impactPoint, out Vector3 impactNormal, out Collider? hitCollider)
    {
        pathPoints[0] = origin;
        pointCount = 1;
        impactPoint = origin;
        impactNormal = Vector3.up;
        hitCollider = null;

        Vector3 previous = origin;
        bool hitSurface = false;
        int segmentCount = Mathf.Max(3, Mathf.CeilToInt(MaxSegments * Mathf.Lerp(0.22f, 1f, streamStrength)));

        for (int i = 1; i < segmentCount; i++)
        {
            float time = i * SegmentTime;
            Vector3 next = origin + velocity * time + Physics.gravity * (0.5f * GravityScale * time * time);
            next += Vector3.right * (Mathf.Sin(Time.time * 18f + seed + i * 0.8f) * 0.01f * i * streamStrength);

            if (TryGetSegmentHit(previous, next, out RaycastHit hit))
            {
                pathPoints[pointCount] = hit.point;
                pointCount++;
                impactPoint = hit.point;
                impactNormal = hit.normal.sqrMagnitude > 0.01f ? hit.normal : Vector3.up;
                hitCollider = hit.collider;
                hitSurface = true;
                break;
            }

            pathPoints[pointCount] = next;
            pointCount++;
            previous = next;
        }

        if (!hitSurface)
        {
            impactPoint = pathPoints[pointCount - 1];
        }

        return hitSurface;
    }

    private bool TryGetSegmentHit(Vector3 from, Vector3 to, out RaycastHit closestHit)
    {
        closestHit = default;
        Vector3 segment = to - from;
        float distance = segment.magnitude;
        if (distance <= 0.001f)
        {
            return false;
        }

        int hitCount = Physics.RaycastNonAlloc(
            from,
            segment / distance,
            raycastHits,
            distance,
            ~0,
            QueryTriggerInteraction.Collide);

        float closestDistance = float.MaxValue;
        bool foundHit = false;

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = raycastHits[i];
            if (hit.collider == null || ShouldIgnoreCollider(hit.collider))
            {
                continue;
            }

            if (hit.distance < closestDistance)
            {
                closestDistance = hit.distance;
                closestHit = hit;
                foundHit = true;
            }
        }

        return foundHit;
    }

    private bool ShouldIgnoreCollider(Collider hitCollider)
    {
        if (player == null)
        {
            return true;
        }

        if (hitCollider.transform == player.transform || hitCollider.transform.IsChildOf(player.transform))
        {
            return true;
        }

        return hitCollider.isTrigger && hitCollider.GetComponentInParent<ItemCharger>() == null;
    }

    private void SetSplashEmission(float rate)
    {
        ParticleSystem.EmissionModule emission = splashParticles.emission;
        emission.rateOverTime = rate;
    }

    private static Material? GetMaterial()
    {
        if (streamMaterial != null)
        {
            return streamMaterial;
        }

        Shader shader = Shader.Find("Sprites/Default")
            ?? Shader.Find("HDRP/Unlit")
            ?? Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Unlit/Color")
            ?? Shader.Find("Standard");

        if (shader == null)
        {
            return null;
        }

        streamMaterial = new Material(shader)
        {
            name = "LethalPisser_StreamMaterial"
        };

        if (streamMaterial.HasProperty("_Color"))
        {
            streamMaterial.SetColor("_Color", streamColor);
        }

        if (streamMaterial.HasProperty("_BaseColor"))
        {
            streamMaterial.SetColor("_BaseColor", streamColor);
        }

        if (streamMaterial.HasProperty("_UnlitColor"))
        {
            streamMaterial.SetColor("_UnlitColor", streamColor);
        }

        return streamMaterial;
    }

    private static AudioClip GetAudioClip()
    {
        if (streamAudioClip != null)
        {
            return streamAudioClip;
        }

        const int sampleRate = 22050;
        const float durationSeconds = 0.45f;
        int sampleCount = Mathf.CeilToInt(sampleRate * durationSeconds);
        float[] samples = new float[sampleCount];
        int noise = 7231;

        for (int i = 0; i < sampleCount; i++)
        {
            noise = unchecked(noise * 1103515245 + 12345);
            float whiteNoise = ((noise >> 16) & 0x7fff) / 16384f - 1f;
            float ripple = Mathf.Sin(i * 0.17f) * 0.012f + Mathf.Sin(i * 0.041f) * 0.008f;
            samples[i] = whiteNoise * 0.018f + ripple;
        }

        streamAudioClip = AudioClip.Create("LethalPisser_StreamLoop", sampleCount, 1, sampleRate, stream: false);
        streamAudioClip.SetData(samples, 0);
        return streamAudioClip;
    }
}
