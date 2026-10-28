; Assemble with: nasm -f bin -Ox generic_dispatch_x86_64.asm -o generic_dispatch_x86_64.bin -l generic_dispatch_x86_64.lst

; Mono shared generic body dispatch stub, see GenericDispatchStub.cs.
; The first 11 bytes load the key into rax, and are replaced by GenericDispatchStub with mov rax, rcx (or rdi);
; test rax, rax; jz +3; mov rax, [rax] (the vtable of this, 0 for null).
; The miss path serves both the Windows and the SysV convention: it saves the argument registers of both, keeps
; the Windows shadow space, and passes the resolver arguments in rcx/rdx as well as rdi/rsi.

BITS 64

dispatch:
    mov rax, r10
    db 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00 ; nop dword [rax + rax + 0]
    mov r11, 0x1111111111111111 ; address of the table pointer
    mov r11, [r11]
.loop:
    cmp qword [r11], 0
    je .miss
    cmp rax, [r11]
    je .hit
    add r11, 16
    jmp .loop
.hit:
    jmp [r11 + 8]
.miss:
    ; entry rsp is 8 mod 16; 7 pushes make it 0 mod 16
    push rdi
    push rsi
    push rcx
    push rdx
    push r8
    push r9
    push r10
    sub rsp, 32 + 8 * 16
    movdqu [rsp + 32], xmm0
    movdqu [rsp + 48], xmm1
    movdqu [rsp + 64], xmm2
    movdqu [rsp + 80], xmm3
    movdqu [rsp + 96], xmm4
    movdqu [rsp + 112], xmm5
    movdqu [rsp + 128], xmm6
    movdqu [rsp + 144], xmm7
    mov rcx, 0x2222222222222222 ; cookie
    mov rdi, rcx
    mov rdx, rax
    mov rsi, rax
    mov rax, 0x3333333333333333 ; resolver
    call rax
    mov r11, rax
    movdqu xmm0, [rsp + 32]
    movdqu xmm1, [rsp + 48]
    movdqu xmm2, [rsp + 64]
    movdqu xmm3, [rsp + 80]
    movdqu xmm4, [rsp + 96]
    movdqu xmm5, [rsp + 112]
    movdqu xmm6, [rsp + 128]
    movdqu xmm7, [rsp + 144]
    add rsp, 32 + 8 * 16
    pop r10
    pop r9
    pop r8
    pop rdx
    pop rcx
    pop rsi
    pop rdi
    jmp r11
