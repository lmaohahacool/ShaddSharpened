using Silk.NET.OpenAL;
using MP3Sharp;
using Shaft.Game.Rendering;

namespace Shaft.Game.Audio;

/// <summary>
/// OpenAL playback for the six sounds in <c>dist/audio.js</c>.
/// <para>
/// The browser build used <c>new Audio(url)</c> elements, which are non-positional and
/// simply play at their <c>.volume</c>. To match, the distance model is set to
/// <c>None</c> so nothing attenuates with range, and each sound keeps its JS gain
/// (break .5, place .5, footstep .2, hit .6, eating .6, music .3).
/// </para>
/// <para>
/// The five short effects are decoded once into a buffer. <c>bg_music.mp3</c> is ~10 MB,
/// which decodes to well over 100 MB of PCM, so it is streamed through a small ring of
/// queued buffers instead and looped by seeking back to the start.
/// </para>
/// <para>
/// A machine with no audio device is a normal case, not an error: initialisation failure
/// leaves <see cref="Available"/> false and every call becomes a no-op, so the game runs
/// silently rather than crashing at the title screen.
/// </para>
/// </summary>
public sealed unsafe class AudioSystem : IDisposable
{
    private const int MUSIC_BUFFERS = 4;
    private const int MUSIC_CHUNK_BYTES = 32768;
    private const int VOICE_COUNT = 8;

    private const int AL_PLAYING = 0x1012;
    private const int AL_STOPPED = 0x1014;

    private ALContext? _alc;
    private AL? _al;
    private Device* _device;
    private Context* _context;

    private readonly Dictionary<string, uint> _buffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _gains = new(StringComparer.Ordinal);
    private readonly uint[] _voices = new uint[VOICE_COUNT];
    private int _voiceNext;

    private MP3Stream? _music;
    private uint _musicSource;
    private readonly uint[] _musicBuffers = new uint[MUSIC_BUFFERS];
    private readonly uint[] _queueScratch = new uint[1];
    private readonly byte[] _musicChunk = new byte[MUSIC_CHUNK_BYTES];
    private int _musicFreq = 44100;
    private BufferFormat _musicFormat = BufferFormat.Stereo16;
    private bool _musicWanted;

    public bool Available { get; private set; }
    public bool MusicEnabled { get; set; } = true;
    public List<string> Problems { get; } = new();

    private readonly string _distRoot;

    public AudioSystem(string distRoot)
    {
        _distRoot = distRoot;
        try
        {
            Init();
        }
        catch (Exception ex)
        {
            Problems.Add($"audio unavailable: {ex.Message}");
            Available = false;
        }
        if (Available) LoadSounds(distRoot);
    }

    private void Init()
    {
        _alc = ALContext.GetApi();
        _al = AL.GetApi();

        _device = _alc.OpenDevice(null);
        if (_device == null)
        {
            Problems.Add("audio unavailable: no OpenAL device");
            return;
        }

        _context = _alc.CreateContext(_device, null);
        if (_context == null || !_alc.MakeContextCurrent(_context))
        {
            Problems.Add("audio unavailable: could not create an OpenAL context");
            return;
        }

        // new Audio() is non-positional; DistanceModel.None reproduces that.
        _al.DistanceModel(DistanceModel.None);

        for (int i = 0; i < VOICE_COUNT; i++) _voices[i] = _al.GenSource();
        Available = true;
    }

    private void LoadSounds(string distRoot)
    {
        foreach ((string key, string rel, float volume, bool loop) in AssetManifest.Sounds)
        {
            _gains[key] = volume;
            if (loop) continue;   // music is streamed, not buffered

            string path = Path.Combine(distRoot, rel);
            if (!File.Exists(path))
            {
                Problems.Add($"missing sound: {rel}");
                continue;
            }

            try
            {
                byte[] pcm = DecodeAll(path, out int freq, out int channels);
                if (pcm.Length == 0) { Problems.Add($"empty decode: {rel}"); continue; }

                uint buf = _al!.GenBuffer();
                fixed (byte* p = pcm)
                {
                    _al.BufferData(buf, channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16,
                                   p, pcm.Length, freq);
                }
                _buffers[key] = buf;
            }
            catch (Exception ex)
            {
                Problems.Add($"could not decode {rel}: {ex.Message}");
            }
        }
    }

    private static byte[] DecodeAll(string path, out int freq, out int channels)
    {
        using var stream = new MP3Stream(path);
        freq = stream.Frequency;
        channels = stream.ChannelCount;
        int chunk = stream.ChunkSize > 0 ? stream.ChunkSize : 8192;
        var buffer = new byte[chunk];
        using var output = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
    }

    // ------------------------------------------------------------------- one-shots

    /// <summary><c>playSound(el)</c> from audio.js: rewind and play at the sound's gain.</summary>
    public void Play(string key)
    {
        if (!Available || _al is null) return;
        if (!_buffers.TryGetValue(key, out uint buffer)) return;

        uint src = NextVoice();
        _al.SourceStop(src);
        _al.SetSourceProperty(src, SourceInteger.Buffer, buffer);
        _al.SetSourceProperty(src, SourceFloat.Gain, _gains.TryGetValue(key, out float g) ? g : 1f);
        _al.SetSourceProperty(src, SourceBoolean.Looping, false);
        _al.SourcePlay(src);
    }

    private uint NextVoice()
    {
        // Prefer a finished voice so overlapping effects do not cut each other off.
        for (int i = 0; i < VOICE_COUNT; i++)
        {
            uint src = _voices[_voiceNext];
            _voiceNext = (_voiceNext + 1) % VOICE_COUNT;
            _al!.GetSourceProperty(src, GetSourceInteger.SourceState, out int state);
            if (state != AL_PLAYING) return src;
        }
        return _voices[(_voiceNext = (_voiceNext + 1) % VOICE_COUNT)];
    }

    // ---------------------------------------------------------------------- music

    /// <summary>Starts (or resumes) the background track if music is enabled.</summary>
    public void StartMusic()
    {
        if (!Available || _al is null) return;
        _musicWanted = true;
        if (!MusicEnabled) return;
        if (_musicSource != 0 && IsMusicPlaying()) return;

        if (_music is null)
        {
            string path = Path.Combine(_distRoot, "sounds", "bg_music.mp3");
            if (!File.Exists(path)) { Problems.Add("missing sound: sounds/bg_music.mp3"); _musicWanted = false; return; }
            try
            {
                _music = new MP3Stream(path);
                _musicFreq = _music.Frequency;
                _musicFormat = _music.ChannelCount == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;
            }
            catch (Exception ex)
            {
                Problems.Add($"could not open bg_music.mp3: {ex.Message}");
                _musicWanted = false;
                return;
            }

            _musicSource = _al.GenSource();
            _al.SetSourceProperty(_musicSource, SourceFloat.Gain, _gains.TryGetValue("music", out float g) ? g : 0.3f);
            _al.SetSourceProperty(_musicSource, SourceBoolean.Looping, false);   // looping is manual
            for (int i = 0; i < MUSIC_BUFFERS; i++) _musicBuffers[i] = _al.GenBuffer();
        }

        _al.SourceStop(_musicSource);
        for (int i = 0; i < MUSIC_BUFFERS; i++)
            if (!FillMusicBuffer(_musicBuffers[i])) break;
        _al.SourceQueueBuffers(_musicSource, _musicBuffers);
        _al.SourcePlay(_musicSource);
    }

    private bool IsMusicPlaying()
    {
        _al!.GetSourceProperty(_musicSource, GetSourceInteger.SourceState, out int state);
        return state == AL_PLAYING;
    }

    /// <summary>
    /// Keeps the queue topped up. Called once per frame; each pass moves at most one
    /// finished buffer back into the queue, which is plenty at 32 KB per buffer.
    /// </summary>
    public void Update()
    {
        if (!Available || _al is null || _music is null || _musicSource == 0) return;

        _al.GetSourceProperty(_musicSource, GetSourceInteger.BuffersProcessed, out int processed);
        while (processed-- > 0)
        {
            _al.SourceUnqueueBuffers(_musicSource, _queueScratch);
            uint buf = _queueScratch[0];
            if (FillMusicBuffer(buf)) _al.SourceQueueBuffers(_musicSource, new[] { buf });
        }

        // The queue can drain completely if the stream stalls; restart it so music loops.
        if (_musicWanted && MusicEnabled && !IsMusicPlaying())
        {
            _al.GetSourceProperty(_musicSource, GetSourceInteger.BuffersQueued, out int queued);
            if (queued == 0) StartMusic();
        }
    }

    private bool FillMusicBuffer(uint buffer)
    {
        if (_music is null || _al is null) return false;
        try
        {
            int read = _music.Read(_musicChunk, 0, _musicChunk.Length);
            if (read <= 0)
            {
                // Loop the track: HTMLAudioElement with .loop wraps back to the start.
                _music.Seek(0, SeekOrigin.Begin);
                read = _music.Read(_musicChunk, 0, _musicChunk.Length);
                if (read <= 0) return false;
            }
            fixed (byte* p = _musicChunk)
            {
                _al.BufferData(buffer, _musicFormat, p, read, _musicFreq);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary><c>toggleMusic()</c>: flips the flag and starts or stops the track.</summary>
    public bool ToggleMusic()
    {
        MusicEnabled = !MusicEnabled;
        if (!Available || _al is null) return MusicEnabled;
        if (MusicEnabled) StartMusic();
        else if (_musicSource != 0) _al.SourceStop(_musicSource);
        return MusicEnabled;
    }

    public void SetMusicEnabled(bool enabled)
    {
        if (MusicEnabled == enabled) return;
        ToggleMusic();
    }

    public void Dispose()
    {
        if (_al is not null)
        {
            foreach (uint v in _voices) if (v != 0) _al.DeleteSource(v);
            foreach (uint b in _buffers.Values) if (b != 0) _al.DeleteBuffer(b);
            if (_musicSource != 0)
            {
                _al.SourceStop(_musicSource);
                _al.DeleteSource(_musicSource);
            }
            foreach (uint b in _musicBuffers) if (b != 0) _al.DeleteBuffer(b);
        }
        _music?.Dispose();
        _music = null;

        if (_alc is not null)
        {
            if (_context != null) { _alc.MakeContextCurrent(null); _alc.DestroyContext(_context); }
            if (_device != null) _alc.CloseDevice(_device);
        }
        _context = null;
        _device = null;
        Available = false;
    }
}
