// ./get-bytes.sh fx_jit_filter
// .NET Framework JIT hook filter, see FxCLR4Runtime and x86_64/fx_jit_filter_x86_64.asm.

.text
.global _main

_main:
    stp x29, x30, [sp, #-0x50]!
    mov x29, sp
    stp x0, x1, [sp, #0x10]
    stp x2, x3, [sp, #0x20]
    stp x4, x5, [sp, #0x30]
    str wzr, [sp, #0x40]
    ldr x0, _host
    add x1, sp, #0x40
    ldr x16, [x0]
    ldr x16, [x16, #0x48] // GetCurrentAppDomainId
    blr x16
    ldr w9, [sp, #0x40]
    ldp x0, x1, [sp, #0x10]
    ldp x2, x3, [sp, #0x20]
    ldp x4, x5, [sp, #0x30]
    ldp x29, x30, [sp], #0x50
    ldr w10, _domain
    ldr x16, _hook
    cmp w9, w10
    b.eq 1f
    ldr x16, _orig
1:
    br x16

_host: .quad 0 // ICLRRuntimeHost*
_hook: .quad 0
_orig: .quad 0 // original compileMethod
_domain: .word 0 // app domain id
