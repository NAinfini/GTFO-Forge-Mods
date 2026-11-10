using System;
using System.Collections.Generic;
using System.Threading;
using ForgeCore;

namespace ForgeReplay;

internal static class DataChunks
{
    /// <summary>A block that continues from the last decoded state of every entity.</summary>
    internal const ushort Delta = 1;
    /// <summary>A block that restores the whole field: every entity in it is written in full, so a jump can start here.</summary>
    internal const ushort Keyframe = 2;
    /// <summary>The last chunk of a finished recording: how the level ended, when, and what every player did (<see cref="RunSummary"/>).</summary>
    internal const ushort Footer = 3;
}

/// <summary>Cuts the stream of samples and events into blocks of about two seconds, and hands each full block to a background thread that
/// compresses and writes it. The main thread only fills a preallocated buffer; there is no allocation per frame. A block that cannot get
/// a free buffer (the writer is behind) keeps its buffer and loses samples, never events, until a buffer frees up; what was lost is
/// counted and reported once. A failed write stops the recording: nothing is retried and no half block is written. Main thread calls
/// <see cref="Tick"/>, <see cref="Sample"/> and <see cref="Event"/>; <see cref="Finish"/> ends it.</summary>
internal sealed class BlockPipeline
{
    private sealed class Job { internal byte[] Buffer = Array.Empty<byte>(); internal int Length; internal uint StartMs, EndMs; internal ushort Type; }

    private readonly ChunkWriter _writer;
    private readonly Action<string> _error;
    private readonly uint _blockMs, _keyframeMs;
    private readonly Action<BlockPipeline, uint> _fullState;
    private readonly SampleEncoder _encoder = new();
    private readonly Stack<byte[]> _free = new();
    private readonly Queue<Job> _queue = new();
    private readonly Thread _thread;
    private readonly object _gate = new();
    private byte[] _current;
    private uint _blockStart, _lastKeyframe;
    private bool _currentIsKeyframe, _keyframeNext, _finishing, _failed;
    private int _lostSamples, _lostEvents, _buffers, _maxQueue;

    // writer: the chunk file to append to. error: called, from either thread, with a message for each failure (once per kind).
    // fullState: called at the start of every keyframe block; it writes every active entity (samples each one) now.
    internal BlockPipeline(ChunkWriter writer, Action<string> error, Action<BlockPipeline, uint> fullState, uint startMs, int buffers = 4, int bufferBytes = 512 * 1024,
        uint blockMs = 2000, uint keyframeMs = 30000)
    {
        _writer = writer; _error = error; _fullState = fullState; _blockMs = blockMs; _keyframeMs = keyframeMs;
        _buffers = buffers;
        for (var i = 1; i < buffers; i++) _free.Push(new byte[bufferBytes]);
        _current = new byte[bufferBytes];
        _thread = new Thread(Run) { IsBackground = true, Name = "ForgeReplay writer" };
        _thread.Start();
        BeginBlock(startMs, keyframe: true);
    }

    internal bool Failed { get { lock (_gate) return _failed; } }
    internal int LostSamples => _lostSamples;
    internal int LostEvents => _lostEvents;
    /// <summary>The most closed blocks that ever waited for the writer at once.</summary>
    internal int MaxQueue { get { lock (_gate) return _maxQueue; } }

    internal bool Sample(uint timeMs, int entity, float x, float y, float z, float qx, float qy, float qz, float qw, byte state)
    {
        if (Failed) return false;
        if (_encoder.Sample(timeMs, entity, x, y, z, qx, qy, qz, qw, state)) return true;
        if (_lostSamples++ == 0) _error("The block buffer is full; samples are being dropped until the writer catches up.");
        return false;
    }

    internal bool Event(uint timeMs, ushort type, ReadOnlySpan<byte> payload)
    {
        if (Failed) return false;
        if (_encoder.Event(timeMs, type, payload)) return true;
        if (_lostEvents++ == 0) _error("The block buffer is full; an event was lost.");
        return false;
    }

    internal void Forget(int entity) => _encoder.Forget(entity);

    /// <summary>Waits until the writer has written every closed block. For tests that run faster than real time.</summary>
    internal void WaitForWriter()
    {
        lock (_gate) while (!_failed && (_queue.Count > 0 || _free.Count < _buffers - 1)) Monitor.Wait(_gate, 50);
    }

    /// <summary>Makes the next <see cref="Tick"/> close the current block whatever its age and open a keyframe block (a checkpoint reload:
    /// the field changed at once, so a jump after it must start from a full state written after it).</summary>
    internal void KeyframeNext() => _keyframeNext = true;

    /// <summary>Closes the current block and opens the next when the block time is up, or at once after <see cref="KeyframeNext"/>.</summary>
    internal void Tick(uint nowMs)
    {
        if (Failed || (!_keyframeNext && nowMs - _blockStart < _blockMs)) return;
        byte[]? next = null;
        lock (_gate) { if (_free.Count > 0) next = _free.Pop(); }
        if (next == null) return; // the writer is behind: keep filling this block, events still fit
        var closed = _current;
        _current = next;
        Close(closed, nowMs);
        BeginBlock(nowMs, keyframe: _keyframeNext || nowMs - _lastKeyframe >= _keyframeMs);
        _keyframeNext = false;
    }

    /// <summary>Writes the last block, waits for the writer to drain, and finishes the file (index and trailer). A failed recording is
    /// not finished: no completion record is written.</summary>
    internal void Finish(uint nowMs, byte[]? footer = null)
    {
        if (!Failed) Close(_current, nowMs);
        lock (_gate) { _finishing = true; Monitor.PulseAll(_gate); }
        _thread.Join();
        if (!Failed)
            try { if (footer != null) _writer.Append(DataChunks.Footer, nowMs, nowMs, footer); _writer.Finish(); }
            catch (Exception error) { Fail("Finishing the recording failed: " + error.Message); }
    }

    private void BeginBlock(uint startMs, bool keyframe)
    {
        _blockStart = startMs; _currentIsKeyframe = keyframe;
        if (keyframe) _lastKeyframe = startMs;
        _encoder.Begin(_current, startMs, keyframe);
        if (keyframe) _fullState(this, startMs);
    }

    private void Close(byte[] buffer, uint endMs)
    {
        var job = new Job { Buffer = buffer, Length = _encoder.Length, StartMs = _blockStart, EndMs = endMs, Type = _currentIsKeyframe ? DataChunks.Keyframe : DataChunks.Delta };
        lock (_gate) { _queue.Enqueue(job); if (_queue.Count > _maxQueue) _maxQueue = _queue.Count; Monitor.PulseAll(_gate); }
    }

    private void Run()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                while (_queue.Count == 0)
                {
                    if (_finishing) return;
                    Monitor.Wait(_gate);
                }
                job = _queue.Dequeue();
            }
            if (!Failed)
                try { _writer.Append(job.Type, job.StartMs, job.EndMs, new ReadOnlySpan<byte>(job.Buffer, 0, job.Length)); }
                catch (Exception error) { Fail("Writing a block failed, the recording stops: " + error.Message); }
            lock (_gate) { if (!ReferenceEquals(job.Buffer, _current)) _free.Push(job.Buffer); Monitor.PulseAll(_gate); }
        }
    }

    private void Fail(string message)
    {
        lock (_gate) { if (_failed) return; _failed = true; }
        _error(message);
    }
}
