# Histórico de versões

## 1.4.0

### Inglês e português

- O app agora segue o idioma do Windows: português quando o Windows está em português, inglês em qualquer outro.
  Vale para tudo: botões, abas, menus, avisos, mensagens de erro, a descrição de cada pasta recriável, datas
  ("há 3 dias" / "3 days ago") e os textos de todos os ajustes do catálogo.
- Plurais em inglês feitos direito ("1 folder", "3 folders"), e números e datas no formato regional do Windows.
- As abas do painel lateral quebram linha em vez de cortar, e o botão da Lixeira encurta o texto com "…" quando
  falta espaço.
- README em inglês ([README.en.md](README.en.md)), com link nos dois sentidos.

### Nomes em inglês no projeto

- Pastas e projetos renomeados: `src/Limpeza` virou `src/Cleanup`, `src/trashsweepr/Limpeza` virou
  `src/trashsweepr/Cleanup`, e o catálogo usa `privacy.json`, `services.json` e `tasks.json`.
- Linha de comando em inglês: `--tab recoverable|changes|largest` e `--cleanup`. Os nomes antigos (`--aba`,
  `--limpeza`) continuam funcionando.
- A pasta de dados `%ProgramData%\trashsweepr\limpeza` não mudou de nome, para o "Desfazer" seguir achando os
  backups de versões anteriores.

### Para quem escreve ajustes

- Os textos do catálogo aceitam `{ "en": "...", "pt-BR": "..." }`. Quando falta a língua do Windows, o app usa a
  mesma língua em outra variante (pt-PT → pt-BR), depois inglês, depois português.
- `TRASHSWEEPR_LANG=en-US` abre o app em outra língua para testar, e `TRASHSWEEPR_TEST_CULTURE` faz o mesmo nos testes.
  Os testes rodam em pt-BR por padrão, seja qual for o idioma da máquina.

## 1.3.1

Versão de acabamento: nada novo na tela, mas bastante coisa corrigida por baixo.

### Disco

- Pastas sem permissão de leitura voltaram a ser detectadas. Antes elas apareciam como vazias, e o aviso
  "pastas sem permissão ignoradas" nunca aparecia.
- Arquivos do OneDrive que estão só na nuvem não contam mais como espaço ocupado no disco.
- A caixa "comparar com" não oferece mais uma varredura que acabou de ser apagada pelo limite de 12 registros.
  Um problema na pasta de registros também não descarta mais uma varredura que já tinha terminado.
- Trocar a comparação enquanto o app atualizava a pasta (F5) ou mandava algo para a Lixeira podia misturar
  os resultados. Agora uma coisa espera a outra.
- Na Lixeira, caminhos longos demais (260 caracteres ou mais) aparecem com o motivo certo, separados do que
  foi cancelado ou estava em uso.
- Depois de usar a Lixeira, a unidade escolhida continua selecionada.
- "Copiar caminho" não fecha mais o app quando outro programa está usando a área de transferência, e erros
  inesperados mostram uma mensagem em vez de fechar a janela.
- O mapa desenha uma vez por navegação (antes eram duas), e o bloco "itens menores" continua selecionado
  ao trocar o modo de cor.

### Limpeza do Windows

- A lista de proteção ficou mais rígida: também bloqueia as chaves de inicialização automática (Run, RunOnce),
  as políticas de UAC e do Explorer, `Session Manager`, `SafeBoot` e os caminhos alternativos
  `WOW6432Node` e `ControlSet00N`, que antes escapavam da checagem.
- O catálogo recusa texto em chaves de comando COM/shell, `deleteKey` que apague algo que o próprio ajuste não
  criou, e `value`/`kind` em ações de exclusão.
- Um campo `null` no catálogo derrubava a carga inteira. Agora só aquele ajuste é descartado, com o motivo no log.
- Um arquivo de backup corrompido ou sem permissão não trava mais todos os "aplicar" e "desfazer".
- Os backups são gravados direto no disco antes de qualquer alteração, para sobreviver a uma queda de energia.
- Ao restaurar a partir do backup, cada alteração é conferida. Se não pegar, o backup continua disponível.
- "Desfazer tudo" desfaz exatamente os ajustes que a confirmação mostrou.
- Em diálogos perigosos (desfazer tudo, seguir sem ponto de restauração), Enter agora cancela em vez de confirmar.
- O botão Cancelar só aparece quando há algo para cancelar, e o "Cancelando…" fica na tela até parar.
- Se uma execução real for cancelada ou der erro, a lista é atualizada mesmo assim.
- O perfil escolhido não muda mais sozinho para "Personalizado" depois de aplicar.
- Quando não dá para reabrir como administrador, a mensagem não diz mais que você recusou o pedido.
- Erros do `sc.exe` mostram o texto do Windows, não só o código.

### Projeto

- GitHub Actions: testes a cada push e release automático ao enviar uma tag `vX.Y.Z`.
- README reescrito.

## 1.3.0

Primeira versão aberta
