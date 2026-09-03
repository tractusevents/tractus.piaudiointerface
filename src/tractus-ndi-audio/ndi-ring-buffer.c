#include "tractus-ndi-audio.h"

#include <math.h>
#include <string.h>

uint64_t tractus_ndi_mono_available(const struct tractus_ndi_mono_ring *ring)
{
    uint64_t write_position = atomic_load_explicit(
        &ring->write_position, memory_order_acquire);
    uint64_t read_position = atomic_load_explicit(
        &ring->read_position, memory_order_relaxed);
    return write_position - read_position;
}

void tractus_ndi_mono_clear(struct tractus_ndi_mono_ring *ring)
{
    uint64_t write_position = atomic_load_explicit(
        &ring->write_position, memory_order_acquire);
    atomic_store_explicit(&ring->read_position, write_position, memory_order_release);
}

void tractus_ndi_mono_push(
    struct tractus_ndi_mono_ring *ring,
    const float *samples,
    uint32_t count)
{
    if (samples == NULL)
        return;
    uint64_t write_position = atomic_load_explicit(
        &ring->write_position, memory_order_relaxed);
    uint64_t read_position = atomic_load_explicit(
        &ring->read_position, memory_order_acquire);
    uint64_t free_samples = TRACTUS_NDI_SEND_RING_CAPACITY -
        (write_position - read_position);
    uint32_t writable = count <= free_samples ? count : (uint32_t)free_samples;
    for (uint32_t sample = 0; sample < writable; sample++) {
        ring->samples[(write_position + sample) & TRACTUS_NDI_SEND_RING_MASK] =
            samples[sample];
    }
    atomic_store_explicit(
        &ring->write_position, write_position + writable, memory_order_release);
    if (writable < count) {
        atomic_fetch_add_explicit(
            &ring->overruns, count - writable, memory_order_relaxed);
    }
}

uint32_t tractus_ndi_mono_pop(
    struct tractus_ndi_mono_ring *ring,
    float *destination,
    uint32_t count)
{
    uint64_t read_position = atomic_load_explicit(
        &ring->read_position, memory_order_relaxed);
    uint64_t write_position = atomic_load_explicit(
        &ring->write_position, memory_order_acquire);
    uint32_t readable = count <= write_position - read_position
        ? count
        : (uint32_t)(write_position - read_position);
    for (uint32_t sample = 0; sample < readable; sample++) {
        destination[sample] =
            ring->samples[(read_position + sample) & TRACTUS_NDI_SEND_RING_MASK];
    }
    atomic_store_explicit(
        &ring->read_position, read_position + readable, memory_order_release);
    return readable;
}

void tractus_ndi_mono_discard(struct tractus_ndi_mono_ring *ring, uint32_t count)
{
    uint64_t read_position = atomic_load_explicit(
        &ring->read_position, memory_order_relaxed);
    uint64_t write_position = atomic_load_explicit(
        &ring->write_position, memory_order_acquire);
    uint64_t readable = write_position - read_position;
    uint64_t discarded = count <= readable ? count : readable;
    atomic_store_explicit(
        &ring->read_position, read_position + discarded, memory_order_release);
}

uint64_t tractus_ndi_stereo_available(const struct tractus_ndi_stereo_ring *ring)
{
    uint64_t write_position = atomic_load_explicit(
        &ring->write_position, memory_order_acquire);
    uint64_t read_position = atomic_load_explicit(
        &ring->read_position, memory_order_relaxed);
    return write_position - read_position;
}

void tractus_ndi_stereo_clear(struct tractus_ndi_stereo_ring *ring)
{
    uint64_t write_position = atomic_load_explicit(
        &ring->write_position, memory_order_acquire);
    atomic_store_explicit(&ring->read_position, write_position, memory_order_release);
}

void tractus_ndi_stereo_push_frame(
    struct tractus_ndi_stereo_ring *ring,
    const NDIlib_audio_frame_v3_t *frame,
    float *peak,
    double *sum_squares,
    uint64_t *meter_samples)
{
    if (frame->p_data == NULL || frame->no_samples <= 0 || frame->no_channels <= 0)
        return;
    uint64_t write_position = atomic_load_explicit(
        &ring->write_position, memory_order_relaxed);
    uint64_t read_position = atomic_load_explicit(
        &ring->read_position, memory_order_acquire);
    uint64_t free_samples = TRACTUS_NDI_RECEIVE_RING_CAPACITY -
        (write_position - read_position);
    uint32_t frame_samples = (uint32_t)frame->no_samples;
    uint32_t writable = frame_samples <= free_samples
        ? frame_samples
        : (uint32_t)free_samples;

    for (unsigned channel = 0; channel < TRACTUS_NDI_CHANNEL_COUNT; channel++) {
        unsigned source_channel = channel < (unsigned)frame->no_channels
            ? channel
            : (unsigned)frame->no_channels - 1U;
        const float *source = (const float *)(frame->p_data +
            source_channel * (unsigned)frame->channel_stride_in_bytes);
        for (uint32_t sample = 0; sample < writable; sample++) {
            float value = source[sample];
            ring->samples[channel]
                [(write_position + sample) & TRACTUS_NDI_RECEIVE_RING_MASK] = value;
            float absolute = fabsf(value);
            if (absolute > *peak)
                *peak = absolute;
            *sum_squares += (double)value * value;
        }
    }
    *meter_samples += (uint64_t)writable * TRACTUS_NDI_CHANNEL_COUNT;
    atomic_store_explicit(
        &ring->write_position, write_position + writable, memory_order_release);
    if (writable < frame_samples) {
        atomic_fetch_add_explicit(
            &ring->overruns, frame_samples - writable, memory_order_relaxed);
    }
}

void tractus_ndi_stereo_pop(
    struct tractus_ndi_stereo_ring *ring,
    float *destinations[TRACTUS_NDI_CHANNEL_COUNT],
    uint32_t count)
{
    uint64_t read_position = atomic_load_explicit(
        &ring->read_position, memory_order_relaxed);
    uint64_t write_position = atomic_load_explicit(
        &ring->write_position, memory_order_acquire);
    uint32_t readable = count <= write_position - read_position
        ? count
        : (uint32_t)(write_position - read_position);
    for (unsigned channel = 0; channel < TRACTUS_NDI_CHANNEL_COUNT; channel++) {
        if (destinations[channel] == NULL)
            continue;
        for (uint32_t sample = 0; sample < readable; sample++) {
            destinations[channel][sample] = ring->samples[channel]
                [(read_position + sample) & TRACTUS_NDI_RECEIVE_RING_MASK];
        }
        if (readable < count) {
            memset(destinations[channel] + readable, 0,
                (count - readable) * sizeof(float));
        }
    }
    atomic_store_explicit(
        &ring->read_position, read_position + readable, memory_order_release);
    if (readable < count) {
        atomic_fetch_add_explicit(
            &ring->underruns, count - readable, memory_order_relaxed);
    }
}
