// A physical controller's face buttons as Switch buttons, shared by the Mac front end (front_mac.m) and the iOS gamepad (DmGamepad.m). GameController names most pads' face
// buttons by position, as on an Xbox pad (buttonA bottom, buttonB right, buttonX left, buttonY top), but a Nintendo pad's by their printed labels (buttonA is the right
// button, marked A): a Switch Pro Controller (or a pad that presents itself as one) and Joy-Con (productCategory "Nintendo Switch Joy-Con (L)", "(R)", "(L/R)").
// The Switch's layout is right A, bottom B, top X, left Y, so a Nintendo pad maps name for name and any other pad by position. Checked with a Pro Controller on the Mac
// and on iOS (PR #650). Open: a single Joy-Con held sideways is matched too and has not been tried; if GameController keeps its labels there instead of turning them
// with the controller, its face buttons come out a quarter turn off.
#pragma once
#import <GameController/GameController.h>
#include <stdint.h>
#include <stdlib.h>
#include "dm_input.h"

static inline int dm_pad_labelled(GCController *c) {
    NSString *k = c.productCategory;
    return [k isEqualToString:@"Switch Pro Controller"] || [k containsString:@"Joy-Con"];
}
// DM_PAD_SWAP=1 exchanges A with B and X with Y after that mapping, for every pad: for a player used to a pad's printed labels (a DualSense's bottom button
// confirms as A, its right one cancels as B, left X, top Y) rather than the Switch's positions; read once
static inline int dm_pad_swap_on(void) {
    static int v = -1;
    if (v < 0) { const char *e = getenv("DM_PAD_SWAP"); v = e && atoi(e) ? 1 : 0; }
    return v;
}
// DM_BTN_A / B / X / Y of the pad's pressed face buttons; swap as DM_PAD_SWAP
static inline uint64_t dm_pad_face_swap(GCExtendedGamepad *g, int swap) {
    GCControllerButtonInput *a = g.buttonA, *b = g.buttonB, *x = g.buttonX, *y = g.buttonY;
    if (!dm_pad_labelled(g.controller)) { GCControllerButtonInput *t = a; a = b; b = t; t = x; x = y; y = t; }   // by position: right A, bottom B, top X, left Y
    if (swap) { GCControllerButtonInput *t = a; a = b; b = t; t = x; x = y; y = t; }
    return (a.isPressed ? DM_BTN_A : 0) | (b.isPressed ? DM_BTN_B : 0) | (x.isPressed ? DM_BTN_X : 0) | (y.isPressed ? DM_BTN_Y : 0);
}
static inline uint64_t dm_pad_face(GCExtendedGamepad *g) { return dm_pad_face_swap(g, dm_pad_swap_on()); }
