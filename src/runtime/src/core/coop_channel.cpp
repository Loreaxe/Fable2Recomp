// refii — REFII'S CO-OP CHANNEL IS TOLD WHEN ITS PEER LEAVES. The channel (the shadowed XAM net
// layer, xam/xam_net.cpp) learns its peer from the first inbound game datagram and keeps it; the
// title states the end of that peer in its session calls, XSessionLeaveRemote on the host and
// XSessionLeaveLocal on the console that left (the payload's xsession_leave_hooks.cpp). Here, in
// refii_runtime, because the channel's state lives in the runtime the payload links.

#include "core/payload_bridge.h"

extern "C" void RefiiCoopChannelForget(void);

extern "C" void refii_bridge_coop_channel_forget(void) { RefiiCoopChannelForget(); }
