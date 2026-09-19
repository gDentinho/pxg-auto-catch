# PxG Auto Catch v0.10.0

A v0.10.0 mantém o Auto Catch validado da v0.9.0 e adiciona duas camadas para reduzir a dependência de patches manuais quando o PxG muda:

1. **Compatibility Resolver local** — tenta localizar automaticamente os endereços que mudaram no novo `pxgme.exe`.
2. **Launcher/Updater** — antes de abrir o programa, consulta a última GitHub Release e atualiza o aplicativo quando existir uma versão mais nova publicada.

## Uso normal

Depois de compilar, abra:

```text
PxGAutoCatch.exe
```

Esse é o Launcher. Ele:

```text
consulta GitHub Release
→ se houver atualização, baixa o ZIP
→ valida SHA-256
→ atualiza o Core/Bridge
→ abre PxGCorpseReader.exe
```

No Auto Catch, `Conectar ao PxG` continua fazendo tudo automaticamente:

```text
Attach Reader
→ Compatibility Resolver
→ carrega/reutiliza Bridge v6
→ envia perfil de compatibilidade para a Bridge
→ Bridge valida Lua internamente
→ inicia Monitor
```

## Compatibility Resolver

### Build conhecido

Para o build atualmente validado:

```text
SHA-256
9518E6BCD67074FEF4FE812F9BC0BA4A6365B6B8C6F6F40271EBDF62135F8236
```

é usado o perfil conhecido, mas as estruturas ainda são validadas em runtime.

### Build novo/desconhecido

Quando o SHA muda, o programa não rejeita automaticamente o cliente.

Ele tenta resolver:

```text
Game RVA
Map* RVA
LuaInterface slot RVA
lua_pcall RVA
luaL_loadbufferx RVA
```

por assinaturas do código do executável e referências RIP-relative.

Depois executa validações **read-only**:

```text
Game
→ LocalPlayer válido
→ Creature layout coerente
→ nome/HP/posição plausíveis

Map
→ Map* válido
→ hash/list de Creatures coerente

Lua
→ LuaInterface* válido
→ lua_State* válido
→ stack base/top/max coerentes
```

Somente depois o perfil é aceito.

A Bridge v6 recebe os RVAs resolvidos pelo Reader e faz uma segunda validação interna executando um chunk Lua trivial antes de liberar `UseBallOnCorpse`.

Se qualquer etapa for ambígua ou falhar:

```text
Auto Catch NÃO é liberado
```

O programa não tenta adivinhar um endereço.

## Cache automático

Um build novo que foi resolvido e validado é salvo em:

```text
%LocalAppData%\PxGCorpseReader\compatibility_profiles.json
```

Na próxima abertura, o perfil é carregado e **revalidado** antes de ser usado.

Isso significa que uma atualização comum do PxG que apenas mova funções/objetos pode ser absorvida sem baixar um patch novo do Auto Catch.

## Limite do auto-resolver

Ele foi feito para mudanças como:

```text
Game mudou de RVA
Map mudou de RVA
LuaInterface mudou de RVA
funções Lua foram relinkadas para outro endereço
```

Ele não promete resolver automaticamente uma reescrita estrutural do cliente. Se o PxG alterar, por exemplo:

```text
layout de Creature
layout do Map/Tile
estrutura do lua_State
API g_game/g_map
semântica de useInventoryItemWith
```

as validações devem falhar e a automação fica bloqueada. Isso é intencional.

## Bridge v6

A Bridge agora usa protocolo v6:

```text
PxGCorpseBridge_v6.dll
PxGCorpseBridge.<PID>.v6
```

Nova ação:

```text
ConfigureCompatibility
```

O Reader envia:

```text
LuaInterfaceSlotRva
LuaPCallRva
LuaLoadBufferXRva
```

A Bridge só libera o Catch depois de validar esse perfil.

A antiga Bridge v5 pode permanecer carregada no mesmo processo durante o primeiro teste; a v6 usa outro nome de DLL e outro pipe.

## Launcher / Updater pelo GitHub

O Launcher está configurado por padrão para:

```text
gDentinho/pxg-auto-catch
```

Configuração:

```text
update_config.json
```

Conteúdo padrão:

```json
{
  "enabled": true,
  "githubOwner": "gDentinho",
  "githubRepo": "pxg-auto-catch",
  "appExe": "PxGCorpseReader.exe",
  "timeoutSeconds": 8
}
```

Se o repositório ainda não existir ou a internet estiver indisponível, o Launcher registra o erro em `launcher.log` e abre a versão instalada normalmente.

### Formato esperado da GitHub Release

O Launcher consulta:

```text
GET /repos/<owner>/<repo>/releases/latest
```

Ele procura dois assets:

```text
PxGAutoCatch-vX.Y.Z-win-x64.zip
PxGAutoCatch-vX.Y.Z-win-x64.zip.sha256
```

Antes de instalar, o SHA-256 do ZIP é comparado com o arquivo `.sha256`.

## GitHub Actions incluído

O projeto contém:

```text
.github/workflows/release.yml
build_ci.ps1
```

Ao criar uma tag como:

```text
v0.10.0
```

o GitHub Actions compila:

```text
Core .NET
Bridge C++ x64
Launcher
```

cria o ZIP e o `.sha256` e publica ambos automaticamente na GitHub Release.

Assim o Launcher passa a encontrar a nova versão sem editar um manifest manualmente.

## Build local

Execute:

```text
build_release.bat
```

Ele gera:

```text
publish_YYYYMMDD_HHMMSS\PxGAutoCatch.exe
publish_YYYYMMDD_HHMMSS\PxGCorpseReader.exe
publish_YYYYMMDD_HHMMSS\PxGCorpseBridge_v6.dll
publish_YYYYMMDD_HHMMSS\update_config.json
```

Também gera na raiz:

```text
PxGAutoCatch-v0.10.0-win-x64.zip
```

junto do SHA-256 mostrado no final do build.

## Interface

A interface simplificada da v0.9.0 foi preservada:

```text
Auto Catch
├── Conectar ao PxG
├── Ativar Auto Catch contínuo
├── Delay após corpse
├── Pokémon + Ball
└── Lista Pokémon | Ball | Remover
```

Detalhes técnicos, catálogo de Item IDs e logs continuam na aba:

```text
Modo desenvolvedor
```

## Persistência

```text
%LocalAppData%\PxGCorpseReader\catch_rules.json
%LocalAppData%\PxGCorpseReader\ball_catalog.json
%LocalAppData%\PxGCorpseReader\settings.json
%LocalAppData%\PxGCorpseReader\compatibility_profiles.json
```

## Observação para quem revisar o projeto

O Compatibility Resolver é fail-closed: ele tenta recuperar endereços após relink/rebuild e valida estruturas antes de habilitar a automação. Não há rotina de esconder DLL, manual mapping, unhooking, alteração de anti-cheat ou mecanismo de evasão. A Bridge continua carregada de forma explícita e a ação de Catch não usa mouse/teclado.
