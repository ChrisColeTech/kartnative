// NVN2 (Switch 2) window: the window builder's and the window's getters, which NVN1 games never called, on the host's window objects (nvn_obj.h win_builder_t / win_t;
// hle_nvn.c builds them, acquires and presents). Registered with NVN2 (nvn_impl.h): only a Switch 2 build (DM_GAME_SWITCH2) uses this file, so Switch 1 games keep
// the red-reporting stub for these names. Facts: docs/reference/nvn2.md#windowbuilder and #window (sdk+0x5cbcd0.. / sdk+0x5cc060.. of Mario Kart World 1.0.0's driver).
// The window's colour space and HDR metadata are not here: MKW never calls them (it sets its HDR through nn::vi, hle_vi.c), and the driver's own window HDR calls end in
// no-ops (nn::wsi::nv::OnDisplayMetadataUpdate, sdk+0x541b40).
#include "../../host/hle.h"
#include "nvn_impl.h"
#include "nvn_obj.h"

#define CWB(b) ((const win_builder_t *)(b))
#define CW(w) ((const win_t *)(w))

NVN2(nvnWindowBuilderGetDevice, NVNdevice *, const NVNwindowBuilder *b) { return CWB(b)->dev; }
NVN2(nvnWindowBuilderGetNativeWindow, NVNnativeWindow, const NVNwindowBuilder *b) { return CWB(b)->native; }
NVN2(nvnWindowBuilderGetPresentInterval, int, const NVNwindowBuilder *b) { return CWB(b)->interval; }
NVN2(nvnWindowBuilderGetNumTextures, int, const NVNwindowBuilder *b) { return CWB(b)->ntex; }
// sdk+0x5cbdd0: NULL out of range
NVN2(nvnWindowBuilderGetTexture, NVNtexture *, const NVNwindowBuilder *b, int i) { return i >= 0 && i < CWB(b)->ntex ? CWB(b)->tex[i] : NULL; }
// The builder's raw value (sdk+0x5cbe00): 0 = all textures
NVN2(nvnWindowBuilderGetNumActiveTextures, int, const NVNwindowBuilder *b) { return CWB(b)->nactive; }
NVN2(nvnWindowGetNativeWindow, NVNnativeWindow, const NVNwindow *w) { return CW(w)->native; }
NVN2(nvnWindowGetNumTextures, int, const NVNwindow *w) { return CW(w)->ntex; }
// nvnWindowInitialize makes the window's count the builder's, or every texture when the builder left it 0 (sdk+0x5cbeb0)
NVN2(nvnWindowGetNumActiveTextures, int, const NVNwindow *w) { return CW(w)->nactive > 0 ? CW(w)->nactive : CW(w)->ntex; }
