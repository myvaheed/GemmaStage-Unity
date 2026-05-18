using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace GemmaStage.Session.Native;

public sealed class EngineSettingsHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal EngineSettingsHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        GemmaStageNativeMethods.EngineSettingsDelete(handle);
        return true;
    }
}

public sealed class EngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    // Single-permit gate that enforces the "at most one llama_context per
    // engine at a time" invariant the architecture relies on (see
    // docs/SESSION_ARCHITECTURE.md §12). Every conversation acquires this
    // slot before ConversationCreate and releases it after the conversation
    // is disposed, so two KV caches can never be live concurrently against
    // the same engine â€” sequential single-threaded use is guaranteed even
    // if a caller violates the contract by Pushing input from one thread
    // while a Debrief cycle runs on another.
    private readonly SemaphoreSlim _conversationSlot = new(1, 1);
    private readonly object _activeConversationGate = new();
    private readonly HashSet<ConversationHandle> _activeConversations = new();
    private volatile bool _cancellationRequested;

    internal EngineHandle()
        : base(true)
    {
    }

    // Blocks until the conversation slot is free, then returns a lease that
    // releases the slot on Dispose. Pattern at every call site:
    //
    //   using var slot = engine.AcquireConversationSlot();
    //   using var conversation = GemmaStageNative.ConversationCreate(...);
    //   ... send / parse ...
    //
    // The slot stays held until the conversation handle has been disposed,
    // so KV-cache residency cannot overlap across conversations.
    public IDisposable AcquireConversationSlot()
    {
        _conversationSlot.Wait();
        if (_cancellationRequested)
        {
            _conversationSlot.Release();
            throw new ObjectDisposedException(nameof(EngineHandle), "The engine is cancelling.");
        }

        return new ConversationSlot(_conversationSlot);
    }

    public void CancelActiveConversations()
    {
        _cancellationRequested = true;

        ConversationHandle[] snapshot;
        lock (_activeConversationGate)
        {
            snapshot = new ConversationHandle[_activeConversations.Count];
            _activeConversations.CopyTo(snapshot);
        }

        foreach (var conversation in snapshot)
        {
            conversation.CancelProcess();
        }
    }

    public void WaitForConversationSlotToDrain()
    {
        _conversationSlot.Wait();
        _conversationSlot.Release();
    }

    internal void RegisterConversation(ConversationHandle conversation)
    {
        lock (_activeConversationGate)
        {
            _activeConversations.Add(conversation);
        }
    }

    internal void UnregisterConversation(ConversationHandle conversation)
    {
        lock (_activeConversationGate)
        {
            _activeConversations.Remove(conversation);
        }
    }

    protected override bool ReleaseHandle()
    {
        CancelActiveConversations();
        GemmaStageNativeMethods.EngineDelete(handle);
        _conversationSlot.Dispose();
        return true;
    }

    private sealed class ConversationSlot : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public ConversationSlot(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}

public sealed class ConversationConfigHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ConversationConfigHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        GemmaStageNativeMethods.ConversationConfigDelete(handle);
        return true;
    }
}

public sealed class ConversationHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private EngineHandle? _owner;

    internal ConversationHandle()
        : base(true)
    {
    }

    internal void AttachOwner(EngineHandle owner)
    {
        _owner = owner;
        owner.RegisterConversation(this);
    }

    internal void CancelProcess()
    {
        if (IsClosed || IsInvalid)
        {
            return;
        }

        var addedRef = false;
        try
        {
            DangerousAddRef(ref addedRef);
            if (!IsInvalid)
            {
                GemmaStageNativeMethods.ConversationCancelProcess(DangerousGetHandle());
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (addedRef)
            {
                DangerousRelease();
            }
        }
    }

    protected override bool ReleaseHandle()
    {
        Interlocked.Exchange(ref _owner, null)?.UnregisterConversation(this);
        GemmaStageNativeMethods.ConversationDelete(handle);
        return true;
    }
}

public sealed class JsonResponseHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal JsonResponseHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        GemmaStageNativeMethods.JsonResponseDelete(handle);
        return true;
    }
}

public sealed class BenchmarkInfoHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal BenchmarkInfoHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        GemmaStageNativeMethods.BenchmarkInfoDelete(handle);
        return true;
    }
}

