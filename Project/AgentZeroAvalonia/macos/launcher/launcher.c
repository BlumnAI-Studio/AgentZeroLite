// Native launcher for the sandboxed (Mac App Store) bundle. It replaces the .NET apphost as
// Contents/MacOS/AgentZeroLite and does exactly what the apphost does for a self-contained app —
// hostfxr_main_startupinfo with the app folder as the runtime root — with one difference: it is
// linked against AppKit.
//
// Why: inside the App Sandbox, NSApplication's first LaunchServices check-in needs a connection
// made while AppKit is loaded at launch. The apphost does not link AppKit; Avalonia's native
// library loads it later, the check-in then looks up com.apple.coreservices.launchservicesd,
// the sandbox denies it (application.sb has no rule for it) and _RegisterApplication aborts the
// process 270 ms in. Measured with a two-line probe: the same dlopen + sharedApplication crashes
// when AppKit is loaded late and works when the executable links it.
//
// Build: macos/launcher/build.sh (clang, -framework AppKit). Unsandboxed builds keep the apphost.
#include <dlfcn.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// Referenced so the linker keeps the AppKit load command even with -dead_strip.
extern void *NSApp;
void *volatile agentzero_keep_appkit = &NSApp;

typedef int (*hostfxr_main_startupinfo_fn)(int argc, const char **argv, const char *host_path,
                                           const char *dotnet_root, const char *app_path);

int main(int argc, const char **argv) {
  char raw[PATH_MAX], exe[PATH_MAX];
  uint32_t size = sizeof raw;
  if (_NSGetExecutablePath(raw, &size) != 0 || !realpath(raw, exe)) {
    fprintf(stderr, "[launcher] cannot resolve the executable path\n");
    return 140;
  }

  char dir[PATH_MAX];
  strncpy(dir, exe, sizeof dir);
  char *slash = strrchr(dir, '/');
  if (!slash) return 140;
  *slash = '\0';

  char hostfxr[PATH_MAX], app[PATH_MAX];
  snprintf(hostfxr, sizeof hostfxr, "%s/libhostfxr.dylib", dir);
  snprintf(app, sizeof app, "%s/AgentZeroLite.dll", dir);

  void *lib = dlopen(hostfxr, RTLD_NOW | RTLD_LOCAL);
  if (!lib) {
    fprintf(stderr, "[launcher] %s\n", dlerror());
    return 141;
  }
  hostfxr_main_startupinfo_fn run = (hostfxr_main_startupinfo_fn)dlsym(lib, "hostfxr_main_startupinfo");
  if (!run) {
    fprintf(stderr, "[launcher] hostfxr_main_startupinfo not found\n");
    return 142;
  }
  return run(argc, argv, exe, dir, app);
}
