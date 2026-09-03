#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

#ifndef SOCK_CLOEXEC
#define SOCK_CLOEXEC 02000000
#endif
#ifndef SOCK_NONBLOCK
#define SOCK_NONBLOCK 00004000
#endif
#ifndef MSG_NOSIGNAL
#define MSG_NOSIGNAL 0
#endif

#define ROOT_ENV "SMAPI_LINUX_GUI_HARD_STATE_ROOT"
#define PID_FILE_ENV "SMAPI_LINUX_GUI_HARD_STATE_PID_FILE"
#define SOCKET_ENV "SMAPI_LINUX_GUI_HARD_STATE_SOCKET"
#define TIMEOUT_ENV "SMAPI_LINUX_GUI_HARD_STATE_TIMEOUT_MS"
#define DISPOSABLE_MARKER ".smapi-hard-state-disposable"
#define DISPOSABLE_MARKER_CONTENT "SMAPI Linux GUI hard-state disposable root v1\n"
#define MAX_JOURNAL_BYTES (64LL * 1024LL * 1024LL)
#define MAX_EVENT_LINE_BYTES 2048
#define MIN_TIMEOUT_MS 100
#define MAX_TIMEOUT_MS 30000

static _Atomic int barrier_fired = 0;

struct barrier_config
{
    char root[PATH_MAX];
    char socket_path[sizeof(((struct sockaddr_un *)0)->sun_path)];
    int timeout_ms;
};

static bool exact_mode(mode_t mode, mode_t expected)
{
    return (mode & 07777) == expected;
}

static bool is_lower_hex(char value)
{
    return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');
}

static bool consume_literal(const char **cursor, const char *end, const char *literal)
{
    size_t length = strlen(literal);
    if ((size_t)(end - *cursor) < length || memcmp(*cursor, literal, length) != 0)
        return false;
    *cursor += length;
    return true;
}

static bool consume_unsigned(const char **cursor, const char *end, unsigned maximum, unsigned *value)
{
    const char *current = *cursor;
    unsigned parsed = 0;
    if (current == end || *current < '0' || *current > '9')
        return false;
    if (*current == '0' && current + 1 < end && current[1] >= '0' && current[1] <= '9')
        return false;
    while (current < end && *current >= '0' && *current <= '9')
    {
        unsigned digit = (unsigned)(*current - '0');
        if (parsed > (maximum - digit) / 10)
            return false;
        parsed = parsed * 10 + digit;
        current++;
    }
    *cursor = current;
    *value = parsed;
    return true;
}

static bool consume_digest(const char **cursor, const char *end)
{
    if ((size_t)(end - *cursor) < 64)
        return false;
    for (size_t index = 0; index < 64; index++)
    {
        if (!is_lower_hex((*cursor)[index]))
            return false;
    }
    *cursor += 64;
    return true;
}

static bool parse_applied_event(const char *line, size_t length, unsigned *operation_index)
{
    const char *cursor = line;
    const char *end = line + length;
    unsigned ignored;
    if (!consume_literal(&cursor, end, "{\"schemaVersion\":1,\"sequence\":"))
        return false;
    if (!consume_unsigned(&cursor, end, 100000, &ignored))
        return false;
    if (!consume_literal(&cursor, end, ",\"kind\":\"Applied\",\"operationIndex\":"))
        return false;
    if (!consume_unsigned(&cursor, end, 19999, operation_index))
        return false;
    if (!consume_literal(&cursor, end, ",\"planSha256\":\""))
        return false;
    if (!consume_digest(&cursor, end) || !consume_literal(&cursor, end, "\",\"previousEventSha256\":"))
        return false;
    if (!consume_literal(&cursor, end, "\"") || !consume_digest(&cursor, end) || !consume_literal(&cursor, end, "\""))
        return false;
    if (!consume_literal(&cursor, end, ",\"eventSha256\":\""))
        return false;
    if (!consume_digest(&cursor, end) || !consume_literal(&cursor, end, "\"}"))
        return false;
    return cursor == end;
}

static bool read_exact_private_file(const char *path, char *buffer, size_t capacity, size_t *length)
{
    int descriptor = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (descriptor < 0)
        return false;
    struct stat before;
    bool valid = fstat(descriptor, &before) == 0
        && S_ISREG(before.st_mode)
        && before.st_uid == geteuid()
        && before.st_nlink == 1
        && exact_mode(before.st_mode, 0600)
        && before.st_size > 0
        && before.st_size < (off_t)capacity;
    ssize_t count = valid ? read(descriptor, buffer, capacity) : -1;
    struct stat after;
    valid = valid
        && count == before.st_size
        && fstat(descriptor, &after) == 0
        && before.st_dev == after.st_dev
        && before.st_ino == after.st_ino
        && before.st_mode == after.st_mode
        && before.st_uid == after.st_uid
        && before.st_nlink == after.st_nlink
        && before.st_size == after.st_size
        && before.st_mtim.tv_sec == after.st_mtim.tv_sec
        && before.st_mtim.tv_nsec == after.st_mtim.tv_nsec
        && before.st_ctim.tv_sec == after.st_ctim.tv_sec
        && before.st_ctim.tv_nsec == after.st_ctim.tv_nsec;
    close(descriptor);
    if (!valid)
        return false;
    buffer[count] = '\0';
    *length = (size_t)count;
    return true;
}

static bool validate_disposable_root(const char *configured, char *root)
{
    if (configured == NULL || configured[0] != '/' || strlen(configured) >= PATH_MAX)
        return false;
    char resolved[PATH_MAX];
    if (realpath(configured, resolved) == NULL || strcmp(configured, resolved) != 0 || strcmp(resolved, "/") == 0)
        return false;
    struct stat root_stat;
    if (lstat(resolved, &root_stat) != 0
        || !S_ISDIR(root_stat.st_mode)
        || root_stat.st_uid != geteuid()
        || !exact_mode(root_stat.st_mode, 0700))
    {
        return false;
    }
    char marker[PATH_MAX];
    int marker_length = snprintf(marker, sizeof(marker), "%s/%s", resolved, DISPOSABLE_MARKER);
    if (marker_length < 0 || marker_length >= (int)sizeof(marker))
    {
        return false;
    }
    char content[sizeof(DISPOSABLE_MARKER_CONTENT) + 1];
    size_t length = 0;
    if (!read_exact_private_file(marker, content, sizeof(content), &length)
        || length != strlen(DISPOSABLE_MARKER_CONTENT)
        || memcmp(content, DISPOSABLE_MARKER_CONTENT, length) != 0)
    {
        return false;
    }
    memcpy(root, resolved, strlen(resolved) + 1);
    return true;
}

static bool validate_private_socket(const char *path, char *destination)
{
    if (path == NULL || path[0] != '/' || strlen(path) >= sizeof(((struct sockaddr_un *)0)->sun_path))
        return false;
    const char *slash = strrchr(path, '/');
    if (slash == NULL || slash == path || slash[1] == '\0' || strcmp(slash + 1, ".") == 0 || strcmp(slash + 1, "..") == 0)
        return false;
    char parent[PATH_MAX];
    size_t parent_length = (size_t)(slash - path);
    if (parent_length >= sizeof(parent))
        return false;
    memcpy(parent, path, parent_length);
    parent[parent_length] = '\0';
    char resolved_parent[PATH_MAX];
    if (realpath(parent, resolved_parent) == NULL || strcmp(parent, resolved_parent) != 0)
        return false;
    struct stat parent_stat;
    struct stat socket_stat;
    if (lstat(parent, &parent_stat) != 0
        || !S_ISDIR(parent_stat.st_mode)
        || parent_stat.st_uid != geteuid()
        || !exact_mode(parent_stat.st_mode, 0700)
        || lstat(path, &socket_stat) != 0
        || !S_ISSOCK(socket_stat.st_mode)
        || socket_stat.st_uid != geteuid()
        || !exact_mode(socket_stat.st_mode, 0600))
    {
        return false;
    }
    memcpy(destination, path, strlen(path) + 1);
    return true;
}

static bool validate_expected_pid(void)
{
    const char *path = getenv(PID_FILE_ENV);
    if (path == NULL || path[0] != '/' || strlen(path) >= PATH_MAX)
        return false;
    const char *slash = strrchr(path, '/');
    if (slash == NULL || slash == path || slash[1] == '\0')
        return false;
    char parent[PATH_MAX];
    size_t parent_length = (size_t)(slash - path);
    memcpy(parent, path, parent_length);
    parent[parent_length] = '\0';
    char resolved_parent[PATH_MAX];
    struct stat parent_stat;
    if (realpath(parent, resolved_parent) == NULL
        || strcmp(parent, resolved_parent) != 0
        || lstat(parent, &parent_stat) != 0
        || !S_ISDIR(parent_stat.st_mode)
        || parent_stat.st_uid != geteuid()
        || !exact_mode(parent_stat.st_mode, 0700))
    {
        return false;
    }
    char content[32];
    size_t length = 0;
    if (!read_exact_private_file(path, content, sizeof(content), &length) || length < 2 || content[length - 1] != '\n')
        return false;
    if (content[0] == '0')
        return false;
    unsigned parsed = 0;
    for (size_t index = 0; index + 1 < length; index++)
    {
        if (content[index] < '0' || content[index] > '9')
            return false;
        unsigned digit = (unsigned)(content[index] - '0');
        if (parsed > ((unsigned)INT_MAX - digit) / 10)
            return false;
        parsed = parsed * 10 + digit;
    }
    return parsed > 0 && (pid_t)parsed == getpid();
}

static bool load_config(struct barrier_config *config)
{
    if (!validate_expected_pid())
        return false;
    if (!validate_disposable_root(getenv(ROOT_ENV), config->root))
        return false;
    if (!validate_private_socket(getenv(SOCKET_ENV), config->socket_path))
        return false;
    const char *timeout = getenv(TIMEOUT_ENV);
    if (timeout == NULL || *timeout == '\0')
        return false;
    char *end = NULL;
    errno = 0;
    long parsed = strtol(timeout, &end, 10);
    if (errno != 0 || end == timeout || *end != '\0' || parsed < MIN_TIMEOUT_MS || parsed > MAX_TIMEOUT_MS)
        return false;
    config->timeout_ms = (int)parsed;
    return true;
}

static bool exact_events_path(int descriptor, const char *root)
{
    char proc_path[64];
    if (snprintf(proc_path, sizeof(proc_path), "/proc/%ld/fd/%d", (long)getpid(), descriptor) < 0)
        return false;
    char path[PATH_MAX];
    ssize_t length = readlink(proc_path, path, sizeof(path) - 1);
    if (length <= 0 || length >= (ssize_t)sizeof(path) - 1)
        return false;
    path[length] = '\0';

    char prefix[PATH_MAX];
    int prefix_count = snprintf(prefix, sizeof(prefix), "%s/.smapi-installer/transactions/", root);
    if (prefix_count < 0 || prefix_count >= (int)sizeof(prefix))
        return false;
    size_t prefix_length = (size_t)prefix_count;
    static const char suffix[] = "/events.jsonl";
    if ((size_t)length != prefix_length + 32 + sizeof(suffix) - 1
        || memcmp(path, prefix, prefix_length) != 0
        || memcmp(path + prefix_length + 32, suffix, sizeof(suffix) - 1) != 0)
    {
        return false;
    }
    for (size_t index = 0; index < 32; index++)
    {
        if (!is_lower_hex(path[prefix_length + index]))
            return false;
    }
    return true;
}

static bool read_latest_applied(int descriptor, const char *root, unsigned *operation_index)
{
    struct stat before;
    if (fstat(descriptor, &before) != 0
        || !S_ISREG(before.st_mode)
        || before.st_uid != geteuid()
        || before.st_nlink != 1
        || !exact_mode(before.st_mode, 0600)
        || before.st_size <= 0
        || before.st_size > MAX_JOURNAL_BYTES
        || !exact_events_path(descriptor, root))
    {
        return false;
    }

    char tail[MAX_EVENT_LINE_BYTES + 1];
    off_t start = before.st_size > MAX_EVENT_LINE_BYTES ? before.st_size - MAX_EVENT_LINE_BYTES : 0;
    size_t wanted = (size_t)(before.st_size - start);
    ssize_t count;
    do
    {
        count = pread(descriptor, tail, wanted, start);
    }
    while (count < 0 && errno == EINTR);
    if (count != (ssize_t)wanted || wanted < 2 || tail[wanted - 1] != '\n')
        return false;

    size_t line_start = wanted - 1;
    while (line_start > 0 && tail[line_start - 1] != '\n')
        line_start--;
    if (line_start == 0 && start != 0)
        return false;
    size_t line_length = wanted - line_start - 1;
    if (line_length == 0 || line_length >= MAX_EVENT_LINE_BYTES)
        return false;

    struct stat after;
    if (fstat(descriptor, &after) != 0
        || before.st_dev != after.st_dev
        || before.st_ino != after.st_ino
        || before.st_mode != after.st_mode
        || before.st_uid != after.st_uid
        || before.st_nlink != after.st_nlink
        || before.st_size != after.st_size
        || before.st_mtim.tv_sec != after.st_mtim.tv_sec
        || before.st_mtim.tv_nsec != after.st_mtim.tv_nsec
        || before.st_ctim.tv_sec != after.st_ctim.tv_sec
        || before.st_ctim.tv_nsec != after.st_ctim.tv_nsec)
    {
        return false;
    }
    return parse_applied_event(tail + line_start, line_length, operation_index);
}

static int remaining_ms(const struct timespec *deadline)
{
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0)
        return 0;
    int64_t nanoseconds = (int64_t)(deadline->tv_sec - now.tv_sec) * 1000000000LL
        + deadline->tv_nsec - now.tv_nsec;
    if (nanoseconds <= 0)
        return 0;
    int64_t milliseconds = (nanoseconds + 999999) / 1000000;
    return milliseconds > INT_MAX ? INT_MAX : (int)milliseconds;
}

static bool wait_fd(int descriptor, short events, const struct timespec *deadline)
{
    struct pollfd item = { .fd = descriptor, .events = events, .revents = 0 };
    while (true)
    {
        int timeout = remaining_ms(deadline);
        if (timeout <= 0)
            return false;
        int result = poll(&item, 1, timeout);
        if (result > 0)
            return (item.revents & events) != 0 && (item.revents & (POLLERR | POLLNVAL)) == 0;
        if (result == 0)
            return false;
        if (errno != EINTR)
            return false;
    }
}

static void wait_for_release(const struct barrier_config *config, unsigned operation_index)
{
    struct timespec deadline;
    if (clock_gettime(CLOCK_MONOTONIC, &deadline) != 0)
        return;
    deadline.tv_sec += config->timeout_ms / 1000;
    deadline.tv_nsec += (long)(config->timeout_ms % 1000) * 1000000L;
    if (deadline.tv_nsec >= 1000000000L)
    {
        deadline.tv_sec++;
        deadline.tv_nsec -= 1000000000L;
    }

    int connection = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
    if (connection < 0)
        return;
    struct sockaddr_un address;
    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    memcpy(address.sun_path, config->socket_path, strlen(config->socket_path) + 1);
    int connected = connect(connection, (struct sockaddr *)&address, sizeof(address));
    if (connected != 0)
    {
        if (errno != EINPROGRESS || !wait_fd(connection, POLLOUT, &deadline))
        {
            close(connection);
            return;
        }
        int socket_error = 0;
        socklen_t socket_error_length = sizeof(socket_error);
        if (getsockopt(connection, SOL_SOCKET, SO_ERROR, &socket_error, &socket_error_length) != 0 || socket_error != 0)
        {
            close(connection);
            return;
        }
    }

    struct ucred peer;
    socklen_t peer_length = sizeof(peer);
    if (getsockopt(connection, SOL_SOCKET, SO_PEERCRED, &peer, &peer_length) != 0
        || peer_length != sizeof(peer)
        || peer.uid != geteuid())
    {
        close(connection);
        return;
    }

    char request[96];
    int request_length = snprintf(
        request,
        sizeof(request),
        "SMAPI_HARD_STATE_BARRIER_V1 pid=%ld op=%u\n",
        (long)getpid(),
        operation_index
    );
    if (request_length <= 0 || request_length >= (int)sizeof(request))
    {
        close(connection);
        return;
    }
    size_t sent = 0;
    while (sent < (size_t)request_length)
    {
        ssize_t count = send(connection, request + sent, (size_t)request_length - sent, MSG_NOSIGNAL | MSG_DONTWAIT);
        if (count > 0)
        {
            sent += (size_t)count;
            continue;
        }
        if (count < 0 && errno == EINTR)
            continue;
        if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK) && wait_fd(connection, POLLOUT, &deadline))
            continue;
        close(connection);
        return;
    }

    static const char release[] = "release\n";
    char response[32];
    size_t received = 0;
    while (true)
    {
        if (!wait_fd(connection, POLLIN, &deadline))
            break;
        char chunk[16];
        ssize_t count = recv(connection, chunk, sizeof(chunk), MSG_DONTWAIT);
        if (count > 0)
        {
            size_t available = sizeof(response) - received;
            size_t copied = (size_t)count < available ? (size_t)count : available;
            memcpy(response + received, chunk, copied);
            received += copied;
            if (received == sizeof(release) - 1 && memcmp(response, release, sizeof(release) - 1) == 0)
                break;
            continue;
        }
        if (count == 0)
            break;
        if (errno != EINTR && errno != EAGAIN && errno != EWOULDBLOCK)
            break;
    }
    if (received == sizeof(release) - 1 && memcmp(response, release, sizeof(release) - 1) == 0)
    {
        /* Exact release received. */
    }
    close(connection);
}

static void maybe_barrier(int descriptor)
{
    if (atomic_load_explicit(&barrier_fired, memory_order_acquire) != 0)
        return;
    struct barrier_config config;
    if (!load_config(&config))
        return;
    unsigned operation_index = 0;
    if (!read_latest_applied(descriptor, config.root, &operation_index))
        return;
    int expected = 0;
    if (!atomic_compare_exchange_strong_explicit(
        &barrier_fired,
        &expected,
        1,
        memory_order_acq_rel,
        memory_order_acquire
    ))
    {
        return;
    }
    wait_for_release(&config, operation_index);
}

int fsync(int descriptor)
{
    int result = (int)syscall(SYS_fsync, descriptor);
    int saved_errno = errno;
    if (result == 0)
        maybe_barrier(descriptor);
    errno = saved_errno;
    return result;
}

int fdatasync(int descriptor)
{
    int result = (int)syscall(SYS_fdatasync, descriptor);
    int saved_errno = errno;
    if (result == 0)
        maybe_barrier(descriptor);
    errno = saved_errno;
    return result;
}
