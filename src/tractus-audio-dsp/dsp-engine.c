#include "tractus-audio-dsp.h"

#include <math.h>
#include <stdio.h>

#define METER_RATE_HZ 10.0f

static float linear_to_dbfs(float linear)
{
    return linear <= 0.000001f ? -120.0f : 20.0f * log10f(linear);
}

static float smoothing_coefficient(float milliseconds, float sample_rate)
{
    float samples = fmaxf(1.0f, milliseconds * 0.001f * sample_rate);
    return 1.0f - expf(-1.0f / samples);
}

void tractus_dsp_parameters_initialize(struct tractus_dsp_parameters *parameters)
{
    atomic_init(&parameters->master_gain, 1.0f);
    atomic_init(&parameters->duck_enabled, false);
    atomic_init(&parameters->trigger_mask, 1U << 1);
    atomic_init(&parameters->threshold_dbfs, -30.0f);
    atomic_init(&parameters->duck_depth_db, 18.0f);
    atomic_init(&parameters->attack_ms, 10.0f);
    atomic_init(&parameters->hold_ms, 150.0f);
    atomic_init(&parameters->release_ms, 400.0f);
    for (unsigned device = 0; device < TRACTUS_DSP_DEVICE_COUNT; device++) {
        atomic_init(&parameters->output_enabled[device], true);
        atomic_init(&parameters->output_gain[device], 1.0f);
        atomic_init(&parameters->output_solo[device], false);
    }
    atomic_init(&parameters->sidetone_enabled, false);
    atomic_init(&parameters->sidetone_gain, 1.0f);
    atomic_init(&parameters->ndi_receiver_enabled, false);
    atomic_init(&parameters->ndi_receiver_gain, 1.0f);
}

static void publish_meter_frame(struct tractus_dsp_data *data)
{
    uint32_t sequence = atomic_load_explicit(
        &data->meter_write_sequence, memory_order_relaxed) + 1;
    struct tractus_dsp_meter_frame *frame =
        &data->meter_ring[sequence % TRACTUS_DSP_METER_RING_SIZE];
    double source_denominator =
        (double)data->meter_frames * TRACTUS_DSP_CHANNEL_COUNT;
    double mix_denominator =
        (double)data->meter_frames * TRACTUS_DSP_CHANNEL_COUNT;

    frame->sequence = sequence;
    bool duck_enabled = atomic_load_explicit(
        &data->parameters.duck_enabled, memory_order_relaxed);
    frame->ducking_active = duck_enabled && data->duck_gain_state < 0.999f;
    frame->duck_gain_reduction_db = duck_enabled
        ? fmaxf(0.0f, -linear_to_dbfs(data->duck_gain_state))
        : 0.0f;
    for (unsigned source = 0; source < TRACTUS_DSP_MIX_SOURCE_COUNT; source++) {
        float rms = source_denominator > 0.0
            ? (float)sqrt(data->meter_source_sum_squares[source] / source_denominator)
            : 0.0f;
        frame->source_peak_dbfs[source] = linear_to_dbfs(data->meter_source_peak[source]);
        frame->source_rms_dbfs[source] = linear_to_dbfs(rms);
        data->meter_source_peak[source] = 0.0f;
        data->meter_source_sum_squares[source] = 0.0;
    }
    float mix_rms = mix_denominator > 0.0
        ? (float)sqrt(data->meter_mix_sum_squares / mix_denominator)
        : 0.0f;
    frame->mix_peak_dbfs = linear_to_dbfs(data->meter_mix_peak);
    frame->mix_rms_dbfs = linear_to_dbfs(mix_rms);
    data->meter_mix_peak = 0.0f;
    data->meter_mix_sum_squares = 0.0;
    data->meter_frames = 0;

    atomic_store_explicit(&data->meter_write_sequence, sequence, memory_order_release);
}

static void on_process(void *userdata, struct spa_io_position *position)
{
    struct tractus_dsp_data *data = userdata;
    uint32_t sample_count = position->clock.duration;
    float *inputs[TRACTUS_DSP_MIX_SOURCE_COUNT][TRACTUS_DSP_CHANNEL_COUNT] = {
        { NULL }
    };
    float *outputs[TRACTUS_DSP_CHANNEL_COUNT];
    bool enabled[TRACTUS_DSP_DEVICE_COUNT];
    bool solo[TRACTUS_DSP_DEVICE_COUNT];
    float gain[TRACTUS_DSP_DEVICE_COUNT];
    bool any_solo = false;

    if (position->clock.rate.num > 0 && position->clock.rate.denom > 0) {
        data->sample_rate = (float)position->clock.rate.denom /
            (float)position->clock.rate.num;
    }
    float sample_rate = data->sample_rate > 1000.0f
        ? data->sample_rate
        : TRACTUS_DSP_DEFAULT_SAMPLE_RATE;

    for (unsigned device = 0; device < TRACTUS_DSP_DEVICE_COUNT; device++) {
        enabled[device] = atomic_load_explicit(
            &data->parameters.output_enabled[device], memory_order_relaxed);
        solo[device] = atomic_load_explicit(
            &data->parameters.output_solo[device], memory_order_relaxed);
        gain[device] = atomic_load_explicit(
            &data->parameters.output_gain[device], memory_order_relaxed);
        any_solo = any_solo || solo[device];
        for (unsigned channel = 0; channel < TRACTUS_DSP_CHANNEL_COUNT; channel++) {
            inputs[device][channel] = pw_filter_get_dsp_buffer(
                data->input_ports[device][channel], sample_count);
        }
    }
    inputs[TRACTUS_DSP_SIDETONE_SOURCE][0] = pw_filter_get_dsp_buffer(
        data->input_ports[TRACTUS_DSP_SIDETONE_SOURCE][0], sample_count);
    for (unsigned channel = 0; channel < TRACTUS_DSP_CHANNEL_COUNT; channel++) {
        inputs[TRACTUS_DSP_NDI_RECEIVER_SOURCE][channel] = pw_filter_get_dsp_buffer(
            data->input_ports[TRACTUS_DSP_NDI_RECEIVER_SOURCE][channel], sample_count);
    }
    for (unsigned channel = 0; channel < TRACTUS_DSP_CHANNEL_COUNT; channel++) {
        outputs[channel] = pw_filter_get_dsp_buffer(
            data->output_ports[channel], sample_count);
        if (outputs[channel] == NULL)
            return;
    }

    bool duck_enabled = atomic_load_explicit(
        &data->parameters.duck_enabled, memory_order_relaxed);
    unsigned trigger_mask = atomic_load_explicit(
        &data->parameters.trigger_mask, memory_order_relaxed);
    float threshold_dbfs = atomic_load_explicit(
        &data->parameters.threshold_dbfs, memory_order_relaxed);
    float duck_depth_db = atomic_load_explicit(
        &data->parameters.duck_depth_db, memory_order_relaxed);
    float attack_ms = atomic_load_explicit(
        &data->parameters.attack_ms, memory_order_relaxed);
    float hold_ms = atomic_load_explicit(
        &data->parameters.hold_ms, memory_order_relaxed);
    float release_ms = atomic_load_explicit(
        &data->parameters.release_ms, memory_order_relaxed);
    float master_gain = atomic_load_explicit(
        &data->parameters.master_gain, memory_order_relaxed);
    bool sidetone_enabled = atomic_load_explicit(
        &data->parameters.sidetone_enabled, memory_order_relaxed);
    float sidetone_gain = atomic_load_explicit(
        &data->parameters.sidetone_gain, memory_order_relaxed);
    bool ndi_receiver_enabled = atomic_load_explicit(
        &data->parameters.ndi_receiver_enabled, memory_order_relaxed);
    float ndi_receiver_gain = atomic_load_explicit(
        &data->parameters.ndi_receiver_gain, memory_order_relaxed);

    float static_gain_coefficient = smoothing_coefficient(5.0f, sample_rate);
    float duck_coefficient = smoothing_coefficient(
        data->duck_active ? attack_ms : release_ms, sample_rate);
    float duck_target = duck_enabled && data->duck_active
        ? powf(10.0f, -duck_depth_db / 20.0f)
        : 1.0f;
    double trigger_sum_squares[TRACTUS_DSP_DEVICE_COUNT + 1U] = { 0.0 };
    uint64_t trigger_samples[TRACTUS_DSP_DEVICE_COUNT + 1U] = { 0 };

    for (uint32_t sample = 0; sample < sample_count; sample++) {
        float mixed[TRACTUS_DSP_CHANNEL_COUNT] = { 0.0f, 0.0f };
        data->duck_gain_state += duck_coefficient *
            (duck_target - data->duck_gain_state);
        data->master_gain_state += static_gain_coefficient *
            (master_gain - data->master_gain_state);

        for (unsigned device = 0; device < TRACTUS_DSP_DEVICE_COUNT; device++) {
            /* Meters are pre-mute, pre-solo, and pre-duck so an incoming host
             * signal remains visible while its mix is muted. */
            float meter_gain = gain[device];
            float audible_target = (enabled[device] ? gain[device] : 0.0f) *
                (!any_solo || solo[device] ? 1.0f : 0.0f);
            data->source_gain_state[device] += static_gain_coefficient *
                (audible_target - data->source_gain_state[device]);

            for (unsigned channel = 0; channel < TRACTUS_DSP_CHANNEL_COUNT; channel++) {
                float raw = inputs[device][channel] == NULL
                    ? 0.0f
                    : inputs[device][channel][sample];
                float metered = raw * meter_gain;
                float absolute_metered = fabsf(metered);
                if (absolute_metered > data->meter_source_peak[device])
                    data->meter_source_peak[device] = absolute_metered;
                data->meter_source_sum_squares[device] += (double)metered * metered;

                float audible = raw * data->source_gain_state[device];
                unsigned trigger_source = device + 1U;
                if ((trigger_mask & (1U << trigger_source)) != 0U) {
                    trigger_sum_squares[trigger_source] += (double)audible * audible;
                    trigger_samples[trigger_source]++;
                }
                float dynamic_gain = (trigger_mask & (1U << trigger_source)) != 0U
                    ? 1.0f
                    : data->duck_gain_state;
                mixed[channel] += audible * dynamic_gain;
            }
        }

        for (unsigned source = TRACTUS_DSP_SIDETONE_SOURCE;
             source < TRACTUS_DSP_MIX_SOURCE_COUNT;
             source++) {
            bool source_enabled = source == TRACTUS_DSP_SIDETONE_SOURCE
                ? sidetone_enabled
                : ndi_receiver_enabled;
            float source_gain = source == TRACTUS_DSP_SIDETONE_SOURCE
                ? sidetone_gain
                : ndi_receiver_gain;
            float audible_target = source_enabled ? source_gain : 0.0f;
            data->source_gain_state[source] += static_gain_coefficient *
                (audible_target - data->source_gain_state[source]);

            for (unsigned channel = 0; channel < TRACTUS_DSP_CHANNEL_COUNT; channel++) {
                float *input = source == TRACTUS_DSP_SIDETONE_SOURCE
                    ? inputs[TRACTUS_DSP_SIDETONE_SOURCE][0]
                    : inputs[TRACTUS_DSP_NDI_RECEIVER_SOURCE][channel];
                float raw = input == NULL ? 0.0f : input[sample];
                if (source == TRACTUS_DSP_SIDETONE_SOURCE && channel == 0 &&
                    (trigger_mask & 1U) != 0U) {
                    trigger_sum_squares[0] += (double)raw * raw;
                    trigger_samples[0]++;
                }
                float metered = raw * source_gain;
                float absolute_metered = fabsf(metered);
                if (absolute_metered > data->meter_source_peak[source])
                    data->meter_source_peak[source] = absolute_metered;
                data->meter_source_sum_squares[source] += (double)metered * metered;
                mixed[channel] += raw * data->source_gain_state[source];
            }
        }

        for (unsigned channel = 0; channel < TRACTUS_DSP_CHANNEL_COUNT; channel++) {
            float output = mixed[channel] * data->master_gain_state;
            outputs[channel][sample] = output;
            float absolute_output = fabsf(output);
            if (absolute_output > data->meter_mix_peak)
                data->meter_mix_peak = absolute_output;
            data->meter_mix_sum_squares += (double)output * output;
        }
    }

    float trigger_block_power = 0.0f;
    for (unsigned source = 0; source <= TRACTUS_DSP_DEVICE_COUNT; source++) {
        if (trigger_samples[source] == 0)
            continue;
        float source_power = (float)(
            trigger_sum_squares[source] / (double)trigger_samples[source]);
        if (source_power > trigger_block_power)
            trigger_block_power = source_power;
    }
    float detector_blend = 1.0f - expf(
        -(float)sample_count / fmaxf(1.0f, 0.010f * sample_rate));
    data->detector_power += detector_blend *
        (trigger_block_power - data->detector_power);
    float detector_dbfs = linear_to_dbfs(
        sqrtf(fmaxf(0.0f, data->detector_power)));

    if (!duck_enabled) {
        data->duck_active = false;
        data->hold_frames_remaining = 0;
    } else if (!data->duck_active && detector_dbfs >= threshold_dbfs) {
        data->duck_active = true;
        data->hold_frames_remaining = (uint64_t)(hold_ms * 0.001f * sample_rate);
    } else if (data->duck_active) {
        if (detector_dbfs >= threshold_dbfs - 3.0f) {
            data->hold_frames_remaining = (uint64_t)(hold_ms * 0.001f * sample_rate);
        } else if (data->hold_frames_remaining > sample_count) {
            data->hold_frames_remaining -= sample_count;
        } else {
            data->hold_frames_remaining = 0;
            data->duck_active = false;
        }
    }

    data->meter_frames += sample_count;
    if ((float)data->meter_frames >= sample_rate / METER_RATE_HZ)
        publish_meter_frame(data);
}

static void on_state_changed(
    void *userdata,
    enum pw_filter_state old_state,
    enum pw_filter_state state,
    const char *error)
{
    (void)userdata;
    (void)old_state;
    fprintf(stderr, "DSP filter state: %s%s%s\n",
        pw_filter_state_as_string(state),
        error == NULL ? "" : " - ",
        error == NULL ? "" : error);
}

const struct pw_filter_events tractus_dsp_filter_events = {
    PW_VERSION_FILTER_EVENTS,
    .state_changed = on_state_changed,
    .process = on_process,
};
