// Exercise the actual P/Invoke boundary without opening any USB device.
#include <errno.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

struct fake_hid {
    int reads;
    int writes;
    int features;
};

void *hid_open_path(const char *path)
{
    (void)path;
    return calloc(1, sizeof(struct fake_hid));
}

int hid_read_timeout(struct fake_hid *device, unsigned char *data, size_t length, int milliseconds)
{
    (void)milliseconds;
    switch (device->reads++) {
    case 0:
        errno = EINTR;
        return -1;
    case 1:
        if (length < 7) {
            errno = EINVAL;
            return -1;
        }
        memset(data, 0, length);
        data[0] = 1;
        data[1] = 1;
        return 7;
    case 2:
        errno = EIO;
        return -1;
    default:
        // hidapi's POLLHUP/disconnected path does not set errno. P/Invoke
        // must not confuse it with an earlier EINTR on the same thread.
        return -1;
    }
}

int hid_write(struct fake_hid *device, const unsigned char *data, size_t length)
{
    (void)data;
    if (device->writes++ == 0) {
        errno = EINTR;
        return -1;
    }
    return (int)length;
}

int hid_send_feature_report(struct fake_hid *device, const unsigned char *data, size_t length)
{
    (void)data;
    if (device->features++ == 0) {
        errno = EINTR;
        return -1;
    }
    return (int)length;
}

const wchar_t *hid_error(struct fake_hid *device)
{
    (void)device;
    return L"Injected HID failure";
}

void hid_close(struct fake_hid *device)
{
    free(device);
}
