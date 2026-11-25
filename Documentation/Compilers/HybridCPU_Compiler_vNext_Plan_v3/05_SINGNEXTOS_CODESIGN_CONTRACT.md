# 05 — Compiler / OS / provider co-design boundary

Этот документ ограничивает compiler изменения, не меняет architecture specifications и не утверждает runtime/OS implementation acceptance.

Existing IrAcceleratorIntent/IrExternalOperationIntent выражают execution contour/domain, secure/virtual intent, memory roles/placement, effects, containment, cancellation, ordering/coherence/publication requests. Descriptor sideband не authority. Семантическая admission rejects несовместимые intent/carrier combinations; нельзя объединять DSC/Matrix/L7 в универсальную execution authority.

| Owner | Ответственность |
|---|---|
| Compiler | static semantic/resource/effect summary и requested constraints |
| OS | capability, Region ownership/generation/lifetime и publication |
| Provider | physical mapping, admission/binding, secure grant |
| Runtime | live Stage A/B legality, execute/commit/retire |

Расширять static summary только для concrete consumer и existing versioned contract. Не сериализовать live owner/capability/provider grant/VMX state как compiler truth и не хранить reusable authorization в compile cache.

SecureCompile evidence/request не выдаёт SecureCompute grant. Virtual-domain/I/O intent не владеет VMCS/VMX; source intrinsic или generator projection не изменяет ISA/runtime legality.

Validation соответствующего slice проверяет no-authority round-trip, secure/virtual required-dependency preservation, DSC/L7 lowering mismatch rejection и отсутствие promotion completion→publication. Требовать actual runtime/provider/OS evidence только для соответствующего execution claim.

Existing architecture/target contracts имеют приоритет над внешними/историческими WhiteBooks. Эта редакция не утверждает, что весь SingNextOS стек был повторно проверен.
