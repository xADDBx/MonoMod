; Assemble with: nasm -f bin -Ox generic_dispatch_x86.asm -o generic_dispatch_x86.bin -l generic_dispatch_x86.lst

; Mono shared generic body dispatch stub, see GenericDispatchStub.cs and generic_dispatch_x86_64.asm.
; Mono passes all arguments on the stack and the generic context in edx.
; The first 10 bytes load the key into eax, and are replaced by GenericDispatchStub with mov eax, [esp + 4] (this);
; test eax, eax; jz +2; mov eax, [eax] (the vtable of this, 0 for null).

BITS 32

dispatch:
    mov eax, edx
    db 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00 ; nop dword [eax + eax + 0]
    mov ecx, [0x11111111] ; address of the table pointer
.loop:
    cmp dword [ecx], 0
    je .miss
    cmp eax, [ecx]
    je .hit
    add ecx, 8
    jmp .loop
.hit:
    jmp [ecx + 4]
.miss:
    push ebp
    mov ebp, esp
    push edx
    ; the resolver may be cdecl or stdcall, esp is restored from ebp either way
    and esp, -16
    sub esp, 8
    push eax
    push 0x22222222 ; cookie
    mov eax, 0x33333333 ; resolver
    call eax
    mov edx, [ebp - 4]
    mov esp, ebp
    pop ebp
    jmp eax
