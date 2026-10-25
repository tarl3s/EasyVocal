# EasyVocal

**Um trocador de voz e painel de sons, grátis e leve, para qualquer chamada.** Fale como robô, monstro ou esquilo
no Discord, no WhatsApp Desktop, nos jogos, em qualquer lugar que use microfone, e toque sons na chamada com uma
tecla. Sem anúncios, sem pagar, nada bloqueado.

[English](README.md)

[**Baixar o EasyVocal para Windows**](https://github.com/tarl3s/EasyVocal/releases/latest/download/EasyVocal-win-x64.zip)
(uns 58 MB, Windows 10 ou 11, não precisa instalar mais nada)

## Configure em 3 passos

1. **Instale o EasyVocal.** Extraia o download em qualquer pasta e abra o `EasyVocal.exe`.
2. **Instale o VB-CABLE de dentro do app.** Na primeira vez o EasyVocal explica e tem um botão **Instalar**.
   Se você não ouvir nada depois de instalar (o VB-CABLE pode virar o alto-falante padrão do PC), o EasyVocal volta o
   som para o seu fone ou caixas de som, ou mostra um botão **Resolver** que faz isso.
3. **Escolha "CABLE Output" como microfone** no app da chamada (Discord, WhatsApp Desktop, seu jogo...). O EasyVocal
   mostra um guia curto para cada app.

Pronto: o que você faz no EasyVocal é o que o outro lado da chamada escuta.

> **A voz só funciona com o EasyVocal aberto.** Fechar a janela manda o app para a bandeja (perto do relógio), onde
> ele continua funcionando. Se você sair de vez, suas chamadas não ouvem nada até você abrir de novo. Nas
> configurações dá para marcar **Iniciar com o Windows** para nunca esquecer.

## O que vem nele

- **14 vozes prontas:** Normal, Feminina, Masculina, Robô, Esquilo, Monstro, Alienígena, Rádio, Demônio, Caverna,
  Anão, Gigante, Fantasma e Robô de filme.
- **Suas próprias vozes:** um editor pequeno com 7 controles simples (Mais grave/agudo, Corpo da voz, Robô, Eco,
  Rádio antigo, Distorção, Sussurro). Escute ao vivo com **Me ouvir** enquanto arrasta.
- **Um painel de sons:** 10 sons iniciais que já vêm no app, e os seus: adicione um arquivo de som (MP3, WAV, OGG,
  FLAC, M4A, AAC, WMA, AIFF) ou o som de um vídeo (MP4, MOV, WMV...), ou só arraste e solte, e dê um nome, um emoji e
  um volume. Apertar de novo num som tocando para ele. Dá para tocar vários ao mesmo tempo.
- **Um editor de som** (**✂ Editar áudio...** em qualquer som): veja a onda, escolha um trecho com o mouse e ouça no
  seu fone, e então corte, mantenha só aquele trecho ou divida o som em dois; deixe mais rápido ou mais lento
  (mantendo o tom ou não), mais agudo ou mais grave, mais alto, mais baixo ou o mais alto possível; faça aparecer ou
  sumir aos poucos, toque de trás para frente, coloque eco, passe por qualquer uma das suas vozes ou corte o silêncio
  do começo e do fim. Desfaça e refaça quanto quiser. O seu arquivo original nunca é alterado: **Voltar ao original**
  está sempre a um clique, e **Salvar uma cópia como...** exporta em WAV ou MP3.
- **Atalhos globais** para cada voz e cada som, além de ligar/desligar a voz, parar todos os sons e silenciar o
  microfone. Funcionam com o jogo em tela cheia. (Uma tecla sozinha, sem Ctrl ou Alt, deixa de digitar aquele
  caractere nos outros programas enquanto o EasyVocal roda, e o app avisa.)
- **Ícone na bandeja** com menu rápido de vozes, e **Iniciar com o Windows** (desligado por padrão).
- **Atalho no Menu Iniciar**: na primeira vez que você abre o EasyVocal, ele pergunta se quer adicionar ao Menu Iniciar.
- **Cabe em telas pequenas:** toda janela muda de tamanho, rola em vez de cortar as coisas, abre dentro da sua tela
  e lembra onde você a deixou.
- **English e Português (Brasil).**
- **Leve:** download pequeno, quase nenhum uso de CPU parado, poucos por cento enquanto você fala.
- **Desligado ou Normal = sua voz limpa.** Seus amigos continuam te ouvindo.
- **Vozes de IA: em breve**, como um download opcional separado.

## Créditos

- O **VB-CABLE** é um produto grátis e separado da [VB-Audio](https://vb-audio.com/Cable/) (donationware). O
  EasyVocal não o inclui: ele baixa o instalador oficial da VB-Audio e o abre para você.
- Motor de áudio: [NAudio](https://github.com/naudio/NAudio) (MIT) e NAudio.Vorbis.
- O EasyVocal tem licença MIT. Veja [LICENSE](LICENSE).

## Para desenvolvedores

```
dotnet build
dotnet test                                            # inclui o teste de ponta a ponta (~40 s, mostra o app)
dotnet run --project src/EasyVocal -- --data <pasta de rascunho> --e2e
dotnet run --project src/EasyVocal
```

Mais em [AGENTS.md](AGENTS.md). Release: `tools/Publish-Release.ps1`.
