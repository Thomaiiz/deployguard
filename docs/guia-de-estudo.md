# Guia de estudo do DeployGuard

Este guia acompanha a primeira etapa do projeto. Para cada etapa ele explica o que foi feito, os
comandos usados e os conceitos por trás, e termina com perguntas que costumam aparecer em
entrevistas técnicas, com respostas curtas baseadas neste código.

---

## Etapa 1: estrutura da solução

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet new sln -n DeployGuard` | Cria a *solution*, o arquivo que agrupa projetos .NET (a API e os testes). |
| `dotnet new web -n DeployGuard.Api` | Cria um projeto ASP.NET Core vazio (template de minimal API). |
| `dotnet new xunit -n DeployGuard.Tests` | Cria um projeto de testes com xUnit. |
| `dotnet sln add <projeto>` | Inclui o projeto na solution. |
| `dotnet add <testes> reference <api>` | Faz o projeto de testes enxergar as classes da API. |
| `dotnet add package <nome>` | Adiciona um pacote NuGet (ex.: `Npgsql.EntityFrameworkCore.PostgreSQL`). |
| `dotnet new tool-manifest` + `dotnet tool install dotnet-ef` | Instala a ferramenta de migrations *no repositório* (`dotnet-tools.json`), então todo mundo usa a mesma versão com `dotnet tool restore`. |
| `dotnet new gitignore` | Gera um `.gitignore` padrão para .NET (ignora `bin/`, `obj/` etc.). |

### Conceitos

- **Entidade vs. DTO.** `MonitoredApplication` é a entidade (como o dado vive no banco).
  `ApplicationResponse` é o DTO (o contrato que a API expõe). Separar os dois permite mudar o banco
  sem quebrar clientes e evita expor campos internos, como `NextCheckAt`.
- **EF Core e `DbContext`.** O EF Core é o ORM: traduz LINQ em SQL. O `DbContext` representa uma
  "unidade de trabalho" com o banco. Em `OnModelCreating` configuramos tabela, tamanhos de coluna,
  índices e relacionamentos (*Fluent API*).
- **Enums gravados como texto** (`HasConversion<string>()`). No banco fica `"Down"` e não `2`:
  legível em consultas manuais e seguro se alguém reordenar o enum.
- **Índices compostos.** `(ApplicationId, CheckedAt DESC)` atende exatamente a consulta "histórico
  de uma aplicação, mais recente primeiro". `(IsEnabled, NextCheckAt)` atende o agendador.
- **`Guid.CreateVersion7`.** UUID v7 começa com o timestamp, então IDs novos são "quase
  ordenados". Isso fragmenta menos o índice da chave primária do que um UUID v4 aleatório.

---

## Etapa 2: cadastro de aplicações

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet user-secrets set "ConnectionStrings:DeployGuard" "..."` | Guarda a connection string no perfil do seu usuário (fora da pasta do projeto). Só vale em Development. |
| `dotnet ef migrations add InitialCreate -o Data/Migrations` | Compara o modelo do `DbContext` com o último snapshot e gera o código C# que cria/altera as tabelas. |
| `dotnet run` | Compila e sobe a API. Em Development ela aplica as migrations sozinha. |
| `curl -X POST ... -d '{...}'` | Faz uma requisição HTTP pela linha de comando, útil para testar a API sem frontend. |

### Conceitos

- **Minimal APIs.** Endpoints declarados com `MapGet`, `MapPost` etc. Os parâmetros do método são
  preenchidos automaticamente: rota (`Guid id`), corpo JSON (`CreateApplicationRequest`), serviços
  da injeção de dependência (`DeployGuardDbContext`) e o `CancellationToken` da requisição.
- **`MapGroup`.** Agrupa rotas com um prefixo e políticas em comum (ex.: rate limiting em todo
  `/api`).
- **Injeção de dependência (DI).** Classes recebem o que precisam pelo construtor e o container cria
  tudo. *Lifetimes*: `Singleton` (uma instância para a aplicação inteira), `Scoped` (uma por
  requisição, como o `DbContext`) e `Transient` (uma a cada pedido).
- **Validação e Problem Details.** Erros de entrada voltam como `400` no formato RFC 9457, com um
  dicionário campo → mensagens. O frontend usa isso para mostrar o erro embaixo do campo certo.
- **Códigos HTTP usados.** `201 Created` (com cabeçalho `Location`), `204 No Content` no delete,
  `404`, `400` para validação, `409 Conflict` para limite de aplicações e `429` para rate limiting.
- **`ExecuteDeleteAsync`.** Gera um `DELETE ... WHERE` direto, sem carregar a entidade antes. O
  histórico vai junto pela *foreign key* com `ON DELETE CASCADE`.

---

## Etapa 3: verificações HTTP seguras

Esta é a parte mais importante para explicar em entrevista.

### O problema: SSRF

O DeployGuard faz requisições para URLs que o usuário cadastra. Sem proteção, alguém poderia
cadastrar:

- `http://169.254.169.254/latest/meta-data/`: o serviço de metadados da AWS, que pode devolver
  credenciais temporárias da máquina;
- `http://localhost:5432` ou `http://10.0.0.5/admin`: serviços internos que nunca deveriam ser
  acessados de fora.

O servidor viraria uma "ponte" para dentro da rede. Isso se chama **SSRF (Server-Side Request
Forgery)** e está no OWASP Top 10.

### As camadas de defesa

1. **`UrlSafetyPolicy`**: valida o texto da URL (esquema, credenciais, porta, allowlist de hosts, IP
   literal). Roda no cadastro e de novo antes de cada verificação.
2. **`IpAddressGuard`**: sabe quais faixas de IP não são públicas, usando `System.Net.IPNetwork`
   (notação CIDR, ex.: `10.0.0.0/8`). Trata o caso traiçoeiro de IPv6 que carrega um IPv4 dentro
   (`::ffff:10.0.0.1`).
3. **`SafeSocketConnector`**: plugado no `SocketsHttpHandler.ConnectCallback`, é chamado toda vez
   que o `HttpClient` precisa abrir uma conexão TCP. Resolve o DNS, valida **todos** os IPs e conecta
   neles.

### DNS rebinding e TOCTOU

Por que não basta validar no cadastro? Porque o atacante controla o DNS do domínio dele. No
cadastro `evil.com` responde `93.184.216.34` (público, passa). Minutos depois passa a responder
`127.0.0.1`. Se a validação e a conexão fizessem resoluções de DNS separadas, haveria uma janela
entre **checar** e **usar** (*time-of-check to time-of-use*, TOCTOU). Como o `SafeSocketConnector`
valida os IPs e conecta exatamente neles, essa janela não existe.

### Configuração do `SocketsHttpHandler`

- `AllowAutoRedirect = false`: um destino público poderia responder `302 Location: http://10.0.0.1`.
- `UseProxy = false`: se houvesse proxy, a conexão iria para o IP do proxy e a validação seria
  inútil.
- `HttpCompletionOption.ResponseHeadersRead`: o método retorna quando chegam os cabeçalhos; o corpo
  nunca é baixado.
- `PooledConnectionLifetime`: conexões são reaproveitadas (*connection pooling*), mas recriadas a
  cada 2 minutos, o que revalida o DNS.

### Timeouts e cancelamento

- `ConnectTimeout` limita só a conexão TCP.
- O timeout total é um `CancellationTokenSource` com `CancelAfter`, *linkado* ao token de
  desligamento da aplicação. Assim dá para distinguir "o alvo demorou" (vira `Timeout`) de "a
  aplicação está desligando" (propaga o cancelamento).

### `IHttpClientFactory`

Criar `new HttpClient()` a cada requisição esgota sockets. Um `HttpClient` estático eterno ignora
mudanças de DNS. O `IHttpClientFactory` resolve as duas coisas gerenciando o ciclo de vida dos
handlers, e é onde registramos o handler seguro com nome (`"health-checks"`).

### O agendador (`BackgroundService`)

- `BackgroundService` é um serviço que roda em segundo plano dentro do processo da API, iniciado e
  parado junto com ela.
- `PeriodicTimer` dispara a cada N segundos e não acumula ticks se uma rodada atrasar.
- O agendador é *singleton*, mas o `DbContext` é *scoped*. Por isso ele cria um escopo
  (`IServiceScopeFactory.CreateAsyncScope`) por rodada.
- `Parallel.ForEachAsync` com `MaxDegreeOfParallelism` executa várias verificações ao mesmo tempo
  com um teto. O `DbContext` **não** é usado dentro do laço paralelo, porque não é thread-safe.
- Um `try/catch` por rodada impede que uma falha (ex.: banco fora do ar) mate o serviço.

---

## Etapa 4: histórico e estatísticas

### Conceitos

- **Paginação keyset (por cursor).** Com `OFFSET 10000 LIMIT 50` o banco ainda precisa percorrer
  10.000 linhas, e se chegam dados novos entre uma página e outra, itens se repetem. Keyset diz
  "me dê os 50 seguintes depois de `(CheckedAt, Id)`", e o índice vai direto ao ponto. O `Id` entra
  como desempate quando dois resultados têm o mesmo horário.
- **Cursor opaco.** O cliente recebe uma string em Base64 e devolve como está. Assim dá para mudar
  o formato interno sem quebrar clientes. O cursor é validado (tamanho e conteúdo) porque vem do
  cliente.
- **Pedir `limit + 1`.** Buscamos um item a mais só para saber se existe próxima página, sem fazer
  um `COUNT(*)`.
- **Percentil p95.** "95% das verificações responderam em até X ms". É mais honesto que a média, que
  esconde picos. Calculado pelo método *nearest rank* sobre a lista ordenada. A janela é limitada a
  7 dias, então trazer as latências para a memória é barato.
- **Disponibilidade.** `(Up + Degraded) / total`. `Degraded` respondeu, só que devagar, então conta
  como disponível.

---

## Etapa 5: dashboard Angular

### Comandos

| Comando | O que faz |
|---|---|
| `npx @angular/cli new frontend --zoneless --style=scss --ssr=false` | Cria o projeto Angular sem zone.js, com SCSS e sem renderização no servidor. |
| `npm start` (`ng serve`) | Servidor de desenvolvimento com recarga automática. Usa `proxy.conf.json` para encaminhar `/api` para a API. |
| `npm run build` (`ng build`) | Gera os arquivos estáticos otimizados em `dist/`. |
| `npm run test:ci` (`ng test --watch=false`) | Roda os testes com Vitest uma vez (sem modo observação). |
| `npx prettier --write` | Formata o código num padrão único. |

### Conceitos

- **Standalone components.** Cada componente declara o que usa em `imports`, sem `NgModule`.
- **Signals.** `signal()` guarda um valor reativo, `computed()` deriva valores (ex.: o resumo
  "3 operacionais, 1 fora do ar") e o template se atualiza sozinho quando eles mudam.
- **Zoneless.** Sem zone.js, o Angular não "adivinha" quando algo mudou; ele reage aos signals.
  Menos mágica e melhor desempenho.
- **`OnPush`.** O componente só é re-renderizado quando seus inputs ou signals mudam.
- **RxJS no polling.** `timer(0, 15000)` emite agora e a cada 15 s. `switchMap` troca para a nova
  requisição e cancela a anterior se ainda estiver pendente. `takeUntilDestroyed()` encerra tudo
  quando o usuário sai da tela, evitando *memory leaks*.
- **Reactive Forms.** O formulário é um objeto (`FormGroup`) com validadores. A validação no
  cliente é só conveniência: a regra real está na API, porque o cliente pode ser burlado.
- **`withComponentInputBinding`.** O parâmetro `:id` da rota chega direto como `input()` do
  componente.
- **XSS.** `{{ valor }}` sempre escapa HTML. Um nome como `<img onerror=...>` aparece como texto.

---

## Etapa 6: testes

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet test` | Compila e roda todos os testes xUnit. |
| `DEPLOYGUARD_TEST_CONNECTION=... dotnet test` | Usa um PostgreSQL existente em vez de subir um container. |

### Conceitos

- **Pirâmide de testes.** Muitos testes de unidade (rápidos, isolados) e alguns de integração
  (mais lentos, mais realistas).
- **Test doubles.** `StubHttpMessageHandler` responde sem rede. `FakeHealthChecker` substitui o
  checker nos testes de integração. `FakeTimeProvider` permite "avançar o relógio" e testar latência
  sem `sleep`.
- **`[Theory]` + `[InlineData]`.** Um teste parametrizado: a mesma verificação para dezenas de IPs e
  URLs.
- **`WebApplicationFactory<Program>`.** Sobe a API inteira em memória, com o pipeline real (rate
  limiting, validação, serialização), e dá um `HttpClient` para chamá-la.
- **Banco real nos testes de integração.** Banco em memória ou SQLite se comportam diferente do
  PostgreSQL (tipos, *timezone*, traduções de SQL). **Testcontainers** sobe um PostgreSQL
  descartável em Docker. Cada classe de teste usa um banco com nome aleatório.
- **Teste de segurança que prova o bloqueio.** Em `SafeSocketConnectorTests`, um `TcpListener` real
  fica escutando. O teste faz a requisição e confere que nenhuma conexão chegou ao servidor.

---

## Etapa 7: Docker, Compose e CI

### Comandos

| Comando | O que faz |
|---|---|
| `cp .env.example .env` | Cria o arquivo de segredos local, que o git ignora. |
| `docker compose up --build` | Constrói as imagens e sobe todos os serviços. |
| `docker compose ps` / `logs api` | Mostra o estado dos containers / os logs de um serviço. |
| `docker compose down -v` | Derruba tudo e apaga os volumes (zera o banco). |

### Conceitos

- **Multi-stage build.** A primeira etapa tem o SDK completo para compilar. A segunda só tem o
  runtime e o resultado. A imagem final é menor e não carrega compilador.
- **Cache de camadas.** Copiar o `.csproj` e rodar `restore` antes de copiar o código faz o Docker
  reaproveitar a camada de dependências quando só o código muda.
- **Imagem chiseled.** Ubuntu mínimo sem shell nem gerenciador de pacotes, rodando como não-root.
- **Proxy reverso.** O nginx serve o Angular e encaminha `/api` para a API. Para o navegador é uma
  origem só, então não há CORS.
- **Redes do Compose.** Os serviços se acham pelo nome (`db`, `api`, `demo-app`). Só o `web` publica
  porta, e só em `127.0.0.1`.
- **`healthcheck` + `depends_on: condition: service_healthy`.** A API só sobe depois que o
  PostgreSQL aceita conexões.
- **GitHub Actions.** Um *workflow* com *jobs* que rodam em máquinas limpas. *Service containers*
  sobem um PostgreSQL para os testes. `permissions: contents: read` limita o que o token pode fazer.
- **Dependabot.** Abre PRs automáticos atualizando dependências.

---

## Etapa 8 (segunda fase): incidentes e alertas

### O que mudou

- Duas tabelas novas: `incidents` (quando caiu, quando voltou, por quê) e `alert_notifications`
  (a "caixa de saída" dos alertas).
- `MonitoredApplication` ganhou `ConsecutiveFailures`.
- O agendador, depois de cada verificação, pergunta ao `IncidentTracker` se deve abrir ou fechar um
  incidente.
- Um segundo `BackgroundService`, o `AlertDispatcher`, envia os alertas.
- O dashboard ganhou um banner de incidentes abertos e uma tabela de incidentes no detalhe.

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet ef migrations add AddIncidentsAndAlerts` | Gera a migration com as tabelas novas. Ela é aplicada sozinha quando a API sobe (`MigrateOnStartup`). |
| `git checkout -b feature/alertas` | Cria um branch para a funcionalidade. O código entra na `main` só pelo Pull Request, depois de revisado. |

### Conceitos

- **Máquina de estados.** O incidente tem dois estados (saudável e incidente aberto) e transições
  bem definidas. O `IncidentTracker` é uma função pura: recebe o estado atual e o resultado da
  verificação e devolve o novo estado. Por não acessar banco nem rede, é trivial de testar.
- **Limiar e flapping.** Redes falham por um instante o tempo todo. Se cada falha virasse alerta, o
  time passaria a ignorar os alertas (*alert fatigue*). Exigir N falhas seguidas troca alguns segundos
  de atraso por muito menos ruído.
- **Índice único parcial.** `CREATE UNIQUE INDEX ... ON incidents ("ApplicationId") WHERE
  "ResolvedAt" IS NULL`. Garante no banco que só existe um incidente aberto por aplicação, mesmo que o
  código tenha um bug ou que duas instâncias rodem ao mesmo tempo. Regra importante vira restrição do
  banco, não só `if` no código.
- **Transactional outbox.** O problema: gravar o incidente no banco e enviar a mensagem para o Slack
  são duas operações em sistemas diferentes, e não existe transação que cubra as duas. Se enviar
  primeiro e o banco falhar, o alerta fala de algo que não existe. Se gravar e o Slack estiver fora,
  o alerta se perde. A solução: gravar a *intenção* de enviar (a linha em `alert_notifications`) na
  mesma transação do incidente. Um processo separado lê essa tabela e envia. É o mesmo padrão usado
  para publicar eventos em filas (Kafka, RabbitMQ) em microsserviços.
- **At-least-once vs. exactly-once.** Entregar exatamente uma vez entre sistemas é praticamente
  impossível. Escolhemos "pelo menos uma vez": no pior caso, um alerta duplicado. Quem recebe pode
  usar o `incidentId` para ignorar duplicatas (*idempotência*).
- **Backoff exponencial.** Se o webhook falhou, tentar de novo imediatamente costuma falhar de novo
  e sobrecarrega quem já está com problema. Dobrar a espera a cada tentativa (30 s, 1 min, 2 min...)
  dá tempo para o serviço se recuperar, e um teto de tentativas evita tentar para sempre.
- **Webhook.** É só um `POST` HTTP com JSON para uma URL que o outro sistema fornece. Slack espera
  `{"text": "..."}`, Discord espera `{"content": "..."}`. O formato `Generic` manda todos os campos
  para integrações próprias.
- **`forkJoin` no Angular.** Dispara a busca de aplicações e de incidentes em paralelo e só atualiza
  a tela quando as duas respostas chegam.

---

## Perguntas de entrevista

**Explique o projeto em 30 segundos.**
É uma plataforma de monitoramento: você cadastra aplicações com uma URL de health check, um serviço
em segundo plano verifica cada uma periodicamente, grava disponibilidade, latência e erros no
PostgreSQL, e um dashboard Angular mostra o estado atual e o histórico. A parte mais interessante é
a segurança, porque fazer requisições para URLs do usuário é um vetor clássico de SSRF.

**O que é SSRF e como você mitigou?**
É quando o atacante faz o servidor requisitar um destino interno. Mitiguei em camadas: allowlist de
hosts, bloqueio de IPs privados/loopback/link-local, validação do IP no momento da conexão (contra
DNS rebinding), sem seguir redirects, sem proxy, timeouts e limites de volume.

**Por que validar o IP na conexão e não só no cadastro?**
Por DNS rebinding: o mesmo nome pode resolver para outro IP depois. Validando no `ConnectCallback` e
conectando nos IPs validados, não há intervalo entre checar e usar.

**Como o agendador funciona? E se houver duas instâncias da API?**
`BackgroundService` com `PeriodicTimer`. A cada tick, busca quem tem `NextCheckAt` vencido, roda em
paralelo com teto e grava tudo numa transação. Com duas instâncias, as duas verificariam as mesmas
aplicações. A solução seria `SELECT ... FOR UPDATE SKIP LOCKED` para cada instância "pegar" um lote
diferente, ou mover o agendador para um worker único.

**Por que não usar o `DbContext` dentro do `Parallel.ForEachAsync`?**
Porque o `DbContext` não é thread-safe. As verificações paralelas só produzem resultados em memória;
a gravação acontece depois, numa thread.

**Por que paginação por cursor?**
`OFFSET` degrada com o tamanho da tabela e duplica ou pula itens quando entram dados novos. Keyset
usa o índice e é estável.

**Por que p95 e não só média?**
A média esconde picos. Se 5% das requisições levam 3 segundos, a média pode parecer ótima e o p95
mostra o problema.

**Como você protege segredos?**
Nada no repositório: variáveis de ambiente, `user-secrets` em desenvolvimento, `.env` ignorado pelo
git, Compose falha se a senha não for definida. Também não aceito credenciais dentro da URL e não
logo URLs completas.

**Como o rate limiting sabe o IP real atrás do nginx?**
Pelo `X-Forwarded-For`, mas só quando a requisição vem de uma rede conhecida (o nginx), e o nginx
sobrescreve o cabeçalho. Senão, qualquer cliente forjaria o IP para escapar do limite.

**Por que testar com PostgreSQL real?**
Porque provedores em memória não reproduzem o comportamento do banco (SQL gerado, tipos, fuso
horário). Testcontainers dá um banco descartável e isolado por execução.

**O que você faria a seguir?**
Autenticação (OIDC/JWT), alertas na mudança de estado, Server-Sent Events no dashboard, distribuir o
agendamento entre instâncias e, na visão do produto, OpenTelemetry e correlação com deploys.

**Como você evita perder um alerta se o Slack estiver fora do ar?**
Com o padrão transactional outbox: o alerta é gravado no banco na mesma transação do incidente, e um
serviço separado envia com retentativas e backoff exponencial. A falha do Slack só atrasa o alerta.

**E se o alerta for enviado duas vezes?**
Pode acontecer (entrega "pelo menos uma vez"), e é uma escolha consciente: duplicar é melhor que
perder. O `incidentId` no payload permite ao receptor descartar duplicatas.

**Como você evita alertas falsos?**
Com um limiar de falhas consecutivas antes de abrir o incidente, o que evita *flapping*. Também não
considero "lento" (Degraded) como queda.

**Como garante que não existem dois incidentes abertos para a mesma aplicação?**
Além da lógica no código, um índice único parcial no PostgreSQL. Se algo der errado, o banco recusa a
gravação.

