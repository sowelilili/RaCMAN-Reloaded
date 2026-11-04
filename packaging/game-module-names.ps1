<#
.SYNOPSIS
    The file names of qwark's game modules, for the packaging scripts to dot-source.

.DESCRIPTION
    Since revision 1.26 qwark's core carries no game (a test build's, and since build 57 a
    release's): each is an SPRX of its own, named for its game (qwark's docs/PROTOCOL.md 9.11): rac1.sprx (RaC1) to rac4.sprx (Deadlocked), and tod.sprx
    (Tools of Destruction, revision 1.27). This is the
    one list the scripts use, the same as qwark's title index and makefile and the client's
    GameModule.FileNames, which a unit test holds it to. A game added later is a name here, never a
    number formatted into one.

    $LegacyGameModulePattern matches the names the first test builds of revision 1.26 used,
    game-1.sprx to game-4.sprx, which no module reads any more: the scripts clear them away and a
    release refuses them.
#>

$GameModuleNames = @('rac1.sprx', 'rac2.sprx', 'rac3.sprx', 'rac4.sprx', 'tod.sprx')
$LegacyGameModulePattern = 'game-*.sprx'
