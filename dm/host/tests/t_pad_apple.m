// Face buttons of the shared GameController adapter (pad_apple.h) with fake pads: by position on most pads, by label on a Nintendo pad; no physical controller is needed.
#import "../pad_apple.h"
#include <assert.h>
#include <stdio.h>

@interface TestButton : NSObject
@property BOOL isPressed;
@end
@implementation TestButton
@end
@interface TestController : NSObject
@property NSString *productCategory;
@end
@implementation TestController
@end
@interface TestPad : NSObject
@property TestController *controller;
@property TestButton *buttonA, *buttonB, *buttonX, *buttonY;
@end
@implementation TestPad
@end

// The Switch buttons of a pad of this category with only the named GameController button pressed, with DM_PAD_SWAP's exchange or without
static uint64_t face_swap(NSString *category, char pressed, int swap) {
    TestPad *g = [TestPad new]; g.controller = [TestController new]; g.controller.productCategory = category;
    g.buttonA = [TestButton new]; g.buttonB = [TestButton new]; g.buttonX = [TestButton new]; g.buttonY = [TestButton new];
    TestButton *b = pressed == 'A' ? g.buttonA : pressed == 'B' ? g.buttonB : pressed == 'X' ? g.buttonX : g.buttonY;
    b.isPressed = YES;
    return dm_pad_face_swap((GCExtendedGamepad *)(id)g, swap);
}
static uint64_t face(NSString *category, char pressed) { return face_swap(category, pressed, 0); }

int main(void) { @autoreleasepool {
    for (NSString *k in @[@"Xbox One", @"DualSense", @"HID"]) {   // by position: buttonA bottom (B), buttonB right (A), buttonX left (Y), buttonY top (X)
        assert(face(k, 'A') == DM_BTN_B); assert(face(k, 'B') == DM_BTN_A); assert(face(k, 'X') == DM_BTN_Y); assert(face(k, 'Y') == DM_BTN_X);
    }
    assert(face(nil, 'B') == DM_BTN_A);   // no category: by position
    for (NSString *k in @[@"Switch Pro Controller", @"Nintendo Switch Joy-Con (L/R)", @"Nintendo Switch Joy-Con (R)"]) {   // by label
        assert(face(k, 'A') == DM_BTN_A); assert(face(k, 'B') == DM_BTN_B); assert(face(k, 'X') == DM_BTN_X); assert(face(k, 'Y') == DM_BTN_Y);
    }
    // DM_PAD_SWAP: a DualSense by its labels (bottom A, right B, left X, top Y), a Nintendo pad with A / B and X / Y exchanged
    assert(face_swap(@"DualSense", 'A', 1) == DM_BTN_A); assert(face_swap(@"DualSense", 'B', 1) == DM_BTN_B);
    assert(face_swap(@"DualSense", 'X', 1) == DM_BTN_X); assert(face_swap(@"DualSense", 'Y', 1) == DM_BTN_Y);
    assert(face_swap(@"Switch Pro Controller", 'A', 1) == DM_BTN_B); assert(face_swap(@"Switch Pro Controller", 'X', 1) == DM_BTN_Y);
    TestPad *g = [TestPad new];   // nothing pressed, buttons nil: none
    assert(dm_pad_face((GCExtendedGamepad *)(id)g) == 0);
    printf("t_pad_apple OK\n");
} return 0; }
