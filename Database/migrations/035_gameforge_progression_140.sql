-- Plan and reconstruction boundaries: Gameforge/progression-140/plan.json
-- Official client field normalization. Source facts and row changes are recorded in Gameforge/normalization/.
USE [TGame_gsp];
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;
DECLARE @changed INT=0;
CREATE TABLE #GfChange0 (Id TINYINT PRIMARY KEY,BeforeValue INT NULL,AfterValue INT NULL);
INSERT #GfChange0 VALUES (99,99664347,98715664),
(100,104750278,103801595),
(101,109954099,109005416),
(102,115277347,114328664),
(103,120721550,119772867),
(104,126288255,125339572),
(105,131979003,131030320),
(106,137795343,136846660),
(107,143738830,142790147),
(108,149811017,148862334),
(109,157217406,155064789),
(110,163551694,161399077),
(111,170019383,167866766),
(112,176622051,174469434),
(113,183361278,181208661),
(114,190238647,188086030),
(115,197255746,195103129),
(116,204414167,202261550),
(117,211715505,209562888),
(118,219161361,217008744),
(119,228249830,224600722),
(120,235989543,232340435),
(121,243878600,240229492),
(122,251918618,248269510),
(123,260111222,256462114),
(124,268458033,264808925),
(125,276960681,273311573);
IF EXISTS(SELECT 1 FROM #GfChange0 r LEFT JOIN dbo.[TLEVELCHART] t ON t.[bLevel]=r.Id WHERE t.[bLevel] IS NULL OR NOT(((t.[dwEXP] IS NOT NULL AND r.BeforeValue IS NOT NULL AND t.[dwEXP]=r.BeforeValue) OR (t.[dwEXP] IS NULL AND r.BeforeValue IS NULL)) OR ((t.[dwEXP] IS NOT NULL AND r.AfterValue IS NOT NULL AND t.[dwEXP]=r.AfterValue) OR (t.[dwEXP] IS NULL AND r.AfterValue IS NULL)))) THROW 51000,'Gameforge normalization baseline changed: TLEVELCHART.dwEXP',1;
UPDATE t SET [dwEXP]=r.AfterValue FROM dbo.[TLEVELCHART] t JOIN #GfChange0 r ON t.[bLevel]=r.Id WHERE NOT ((t.[dwEXP] IS NOT NULL AND r.AfterValue IS NOT NULL AND t.[dwEXP]=r.AfterValue) OR (t.[dwEXP] IS NULL AND r.AfterValue IS NULL));
SET @changed=@changed+@@ROWCOUNT;
IF EXISTS(SELECT 1 FROM #GfChange0 r JOIN dbo.[TLEVELCHART] t ON t.[bLevel]=r.Id WHERE NOT ((t.[dwEXP] IS NOT NULL AND r.AfterValue IS NOT NULL AND t.[dwEXP]=r.AfterValue) OR (t.[dwEXP] IS NULL AND r.AfterValue IS NULL))) THROW 51000,'Gameforge normalization postcondition failed: TLEVELCHART.dwEXP',1;
DROP TABLE #GfChange0;
SELECT @changed AS NormalizedOfficialClientFields;
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=126 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 126,281971691,3332,3332,0,9971500,0,1196580,797720,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=126) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (126,281971691,3332,3332,0,9971500,0,1196580,797720,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=127 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 127,290790914,3332,3332,0,10172131,0,1220656,813770,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=127) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (127,290790914,3332,3332,0,10172131,0,1220656,813770,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=128 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 128,299770887,3332,3332,0,10375239,0,1245029,830019,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=128) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (128,299770887,3332,3332,0,10375239,0,1245029,830019,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=129 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 129,308913253,3332,3332,0,10580838,0,1269701,846467,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=129) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (129,308913253,3332,3332,0,10580838,0,1269701,846467,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=130 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 130,318219659,3332,3332,0,10788373,0,1294605,863070,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=130) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (130,318219659,3332,3332,0,10788373,0,1294605,863070,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=131 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 131,327691759,3332,3332,0,10998988,0,1319879,879919,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=131) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (131,327691759,3332,3332,0,10998988,0,1319879,879919,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=132 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 132,337331208,3332,3332,0,11210980,0,1345318,896878,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=132) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (132,337331208,3332,3332,0,11210980,0,1345318,896878,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=133 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 133,347139665,3332,3332,0,11426078,0,1371129,914086,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=133) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (133,347139665,3332,3332,0,11426078,0,1371129,914086,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=134 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 134,357118797,3332,3332,0,11643139,0,1397177,931451,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=134) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (134,357118797,3332,3332,0,11643139,0,1397177,931451,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=135 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 135,367270272,3332,3332,0,11862752,0,1423530,949020,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=135) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (135,367270272,3332,3332,0,11862752,0,1423530,949020,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=136 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 136,377595757,3332,3332,0,12036222,0,1444347,962898,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=136) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (136,377595757,3332,3332,0,12036222,0,1444347,962898,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=137 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 137,388096933,3332,3332,0,12260019,0,1471202,980802,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=137) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (137,388096933,3332,3332,0,12260019,0,1471202,980802,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=138 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 138,398775475,3332,3332,0,12485796,0,1498296,998864,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=138) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (138,398775475,3332,3332,0,12485796,0,1498296,998864,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=139 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 139,409633067,3332,3332,0,12714160,0,1525699,1017133,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=139) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (139,409633067,3332,3332,0,12714160,0,1525699,1017133,17112,34225,125491,969700,57,24715,1042);
IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=140 AND EXISTS(SELECT [bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp] EXCEPT SELECT 140,0,3332,3332,0,12944514,0,1553342,1035561,17112,34225,125491,969700,57,24715,1042)) THROW 51000,'Conflicting high-level chart row',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel=140) INSERT dbo.TLEVELCHART ([bLevel],[dwEXP],[dwHP],[dwMP],[bSkillPoint],[dwMoney],[dwScore],[dwRegCost],[dwSearchCost],[dwGambleCost],[dwRepCost],[dwRepairCost],[dwRefineCost],[wPvPoint],[dwPvPMoney],[dwPvPExp]) VALUES (140,0,3332,3332,0,12944514,0,1553342,1035561,17112,34225,125491,969700,57,24715,1042);
COMMIT TRANSACTION;
GO
