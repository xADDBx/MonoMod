// ./get-bytes.sh generic_dispatch
// Mono shared generic body dispatch stub, see GenericDispatchStub.cs and x86_64/generic_dispatch_x86_64.asm.
// The first 3 instructions load the key into x16: mov x16, x15 (the generic context), or as replaced by GenericDispatchStub
// mov x16, x0; cbz x0, +8; ldr x16, [x0] (the vtable of this, 0 for null). x9 and x17 are scratch; no argument register is touched.

.text
.global _main

_main:
    mov x16, x15
    nop
    nop
    ldr x17, _table
    ldr x17, [x17]
_loop:
    ldr x9, [x17]
    cbz x9, _miss
    cmp x9, x16
    b.eq _hit
    add x17, x17, #16
    b _loop
_hit:
    ldr x9, [x17, #8]
    br x9
_miss:
    stp x29, x30, [sp, #-16]!
    mov x29, sp
    sub sp, sp, #208
    stp x0, x1, [sp, #0]
    stp x2, x3, [sp, #16]
    stp x4, x5, [sp, #32]
    stp x6, x7, [sp, #48]
    stp x8, x15, [sp, #64]
    stp q0, q1, [sp, #80]
    stp q2, q3, [sp, #112]
    stp q4, q5, [sp, #144]
    stp q6, q7, [sp, #176]
    ldr x0, _cookie
    mov x1, x16
    ldr x9, _resolver
    blr x9
    mov x16, x0
    ldp x0, x1, [sp, #0]
    ldp x2, x3, [sp, #16]
    ldp x4, x5, [sp, #32]
    ldp x6, x7, [sp, #48]
    ldp x8, x15, [sp, #64]
    ldp q0, q1, [sp, #80]
    ldp q2, q3, [sp, #112]
    ldp q4, q5, [sp, #144]
    ldp q6, q7, [sp, #176]
    add sp, sp, #208
    ldp x29, x30, [sp], #16
    br x16

.balign 8
_table: .quad 0
_cookie: .quad 0
_resolver: .quad 0
