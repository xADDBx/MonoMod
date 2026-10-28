; Assemble with: nasm -f bin fx_jit_filter_x86.asm -o fx_jit_filter_x86.bin
; .NET Framework JIT hook filter, see FxCLR4Runtime and fx_jit_filter_x86_64.asm. All calls involved are stdcall.

BITS 32

filter:
    push 0
    push esp
    mov eax, 0x11111111 ; ICLRRuntimeHost*
    push eax
    mov eax, [eax]
    call [eax + 0x24] ; GetCurrentAppDomainId
    pop eax
    cmp eax, 0x22222222 ; app domain id
    mov eax, 0x33333333 ; hook
    je .jump
    mov eax, 0x44444444 ; original compileMethod
.jump:
    jmp eax
