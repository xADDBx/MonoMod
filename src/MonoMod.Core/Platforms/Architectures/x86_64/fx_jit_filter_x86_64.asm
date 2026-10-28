; Assemble with: nasm -f bin fx_jit_filter_x86_64.asm -o fx_jit_filter_x86_64.bin
; .NET Framework JIT hook filter, see FxCLR4Runtime. Sends compileMethod calls from the hook's app domain to the hook,
; and all others to the original compileMethod, without touching their arguments.

BITS 64

filter:
    push rcx
    push rdx
    push r8
    push r9
    sub rsp, 0x38
    mov dword [rsp + 0x20], 0
    mov rcx, 0x1111111111111111 ; ICLRRuntimeHost*
    lea rdx, [rsp + 0x20]
    mov rax, [rcx]
    call [rax + 0x48] ; GetCurrentAppDomainId
    mov eax, [rsp + 0x20]
    add rsp, 0x38
    pop r9
    pop r8
    pop rdx
    pop rcx
    cmp eax, 0x22222222 ; app domain id
    mov rax, 0x3333333333333333 ; hook
    je .jump
    mov rax, 0x4444444444444444 ; original compileMethod
.jump:
    jmp rax
