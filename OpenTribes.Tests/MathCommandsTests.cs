// Port of tests/test_math_commands.cpp: math, vector and utility commands (docs/script.md 6.7): sprintf, pow, getRandom,
// timeStamp, which and Vector::*. Expected values in kRows come from the reference server (tests/ref/util.txt is the
// live comparison). The game-state utilities of the C++ test (getIntegerTime, findPointOnSphere, getClientByName,
// getObjectByTargetIndex, readShapeRadius, the kSphere vectors) are game modules: their rows and checks are ported
// with stage P4. kDouble lists the rows whose output differs in the double form of the port (DECISIONS D25: float
// stores and parsing round to nearest, docs/portable-float.md 3) with the output the port prints.
using OpenTribes.Script;

namespace OpenTribes.Tests;

public class MathCommandsTests
{
    private static readonly (string script, string want)[] kRows =
    {
        ("echo(Vector::add(\"1 2 3\",\"4 5 6\"));", "5 7 9"),
        ("echo(Vector::add(\"0.1 0.1 0.1\",\"0.0999999 0.0999999 0.0999999\"));", "0.199999 0.199999 0.199999"),
        ("echo(Vector::add(\"+INF -INF +NAN\",\"1 1 1\"));", "+INF -INF +NAN"),
        ("echo(Vector::add(\"1e 1e+ 1e+5\",\"1 1 1\"));", "2 2 100001"),
        ("echo(Vector::add());", "wrong argcount to Vector::add(vec,vec);\n0 0 0"),
        ("echo(Vector::add(\"-320 182 51.7975\",\"48.854880 383 684\"));", "-271.145 565 735.797"),
        ("echo(Vector::add(\"1e-40 -5.681509e+07 -833301.576\",\"-848 -3992 4.794e-13\"));", "-847.999 -5.6819e+07 -833301"),
        ("echo(Vector::add(\"195 -0.0008 .5\",\"-753 55.0508 79.528\"));", "-558 55.0499 80.0279"),
        ("echo(Vector::add(\"-5901387 260 9.468693e+34\",\"-50.73566 525 -712\"));", "-5.90143e+06 785 9.46869e+34"),
        ("echo(Vector::add(\"-768 9.97e-10 453\",\"-7.76933e+30 703 -74.6132\"));", "-7.76932e+30 703 378.386"),
        ("echo(Vector::sub(\"-0 -0 -0\",\"0 -0 0\"));", "-0 0 -0"),
        ("echo(Vector::sub(\"1e-40 1e-45 1e-39\",\"1e-40 1e-45 1e-39\"));", "0 0 0"),
        ("echo(Vector::sub(\"-NAN 1 1\",\"+NAN 1 1\"));", "+NAN 0 0"),
        ("echo(Vector::sub(\"1 2 3 4 5\",\"1 2 3\"));", "0 0 0"),
        ("echo(Vector::sub(\"1e2 0.1 9.409824e-11\",\"-82 0.004 1e-40\"));", "182 0.0959999 9.40982e-11"),
        ("echo(Vector::sub(\"-1e-40 -302 4.688e-09\",\"277 -49 -6.8689e+25\"));", "-277 -253 6.86889e+25"),
        ("echo(Vector::sub(\"1E-2 102 -73.717879\",\"85.76797 1e-30 6e+04\"));", "-85.7579 101.999 -60073.7"),
        ("echo(Vector::sub(\"3.14159265358979 -8.0808e+30 -17.3\",\"90 13.701557 3.65542922e-11\"));", "-86.8584 -8.08079e+30 -17.2999"),
        ("echo(Vector::sub(\"-7.99318e+09 45.97874 167\",\"-46.1907 -6.726197e-19 813\"));", "-7.99317e+09 45.9787 -646"),
        ("echo(Vector::dot(\"1 2 3\",\"4 5 6\"));", "32"),
        ("echo(Vector::dot(\"0.1 0.1 0.1\",\"0.0999999 0.0999999 0.0999999\"));", "0.0299999"),
        ("echo(Vector::dot(\"+INF -INF +NAN\",\"1 1 1\"));", "+NAN"),
        ("echo(Vector::dot(\"1e 1e+ 1e+5\",\"1 1 1\"));", "100002"),
        ("echo(Vector::dot());", "wrong argcount to dotVec(vec,vec);\n0"),
        ("echo(Vector::dot(\"488 484 -0.0004\",\"70.068491 723 111\"));", "384125"),
        ("echo(Vector::dot(\"1e39 -4.85e-39 6.539e+04\",\"3.66265e-06 -2.9307 185\"));", "+INF"),
        ("echo(Vector::dot(\"-589 -280 -82.896027\",\"-671 2.1e+09 2.48e+31\"));", "-2.05582e+33"),
        ("echo(Vector::dot(\"5.5573996e+09 96.186028 -2.986343\",\"12759.91 -826 -473\"));", "7.09119e+13"),
        ("echo(Vector::dot(\"-33 -64.113 -215\",\"-8.37e+25 -0.9597 -1e+30\"));", "2.15002e+32"),
        ("echo(Vector::getDistance(\"-0 -0 -0\",\"0 -0 0\"));", "0"),
        ("echo(Vector::getDistance(\"1e-40 1e-45 1e-39\",\"1e-40 1e-45 1e-39\"));", "0"),
        ("echo(Vector::getDistance(\"-NAN 1 1\",\"+NAN 1 1\"));", "+NAN"),
        ("echo(Vector::getDistance(\"1 2 3 4 5\",\"1 2 3\"));", "0"),
        ("echo(Vector::getDistance(\"-702 5. -60.78410\",\"435 1.95e-11 -74\"));", "1137.08"),
        ("echo(Vector::getDistance(\"-6.9671e-37 237 1.85e-33\",\"-124.9 -541 0.5\"));", "787.962"),
        ("echo(Vector::getDistance(\"0.3 5.1713 -6.1e+10\",\"3.14159265358979 -231 -3.4028237e38\"));", "+INF"),
        ("echo(Vector::getDistance(\".5 61.901320 -45.316286\",\"-5.7647e+06 -176 -801\"));", "5.7647e+06"),
        ("echo(Vector::getDistance(\"249 1.5762e-39 -839\",\"-9.2131572e+14 -14 39\"));", "9.21315e+14"),
        ("echo(vector::ADD());", "wrong argcount to Vector::add(vec,vec);\n0 0 0"),
        ("echo(Vector::neg(\"0 0 0\"));", "-0 -0 -0"),
        ("echo(Vector::neg(\"1e30 1e30 1e30\"));", "-9.99999e+29 -9.99999e+29 -9.99999e+29"),
        ("echo(Vector::neg(\"+NAN 1 1\"));", "-NAN -1 -1"),
        ("echo(Vector::neg(\"233 -6.993427e-26 5.1e+37\"));", "-233 6.99342e-26 -5.09999e+37"),
        ("echo(Vector::neg(\"-351 389 -5088653\"));", "351 -389 5.08865e+06"),
        ("echo(Vector::neg(\"-1 -0.007843 2.14e+08\"));", "1 0.00784299 -2.14e+08"),
        ("echo(Vector::neg(\"3.160 16777217 -83.456\"));", "-3.15999 -1.67772e+07 83.4559"),
        ("echo(Vector::neg(\"338 79 -416\"));", "-338 -79 416"),
        ("echo(Vector::normalize(\"1 2 3\"));", "0.267261 0.534522 0.801783"),
        ("echo(Vector::normalize(\"1e-40 0 0\"));", "0.034028 0 0"),
        ("echo(Vector::normalize(\"+INF 0 0\"));", "-NAN 0 0"),
        ("echo(Vector::normalize(\"1 2 3\",\"4\"));", "wrong argcount to Vector::normalize(vec);\nfalse"),
        ("echo(Vector::normalize(\"9.362e-25 -7.3e-13 3.14159265358979\"));", "2.98001e-25 -2.32366e-13 0.999999"),
        ("echo(Vector::normalize(\"89.2 6.76e-12 -4.93e+09\"));", "1.80933e-08 1.37119e-21 -0.999999"),
        ("echo(Vector::normalize(\"44.8 -37.50135 -1.63e+14\"));", "2.74846e-13 -2.30069e-13 -0.999999"),
        ("echo(Vector::normalize(\"-720 -4.26264e-10 1.58451261e-12\"));", "-0.999999 -5.92033e-13 2.20071e-15"),
        ("echo(Vector::normalize(\"308 -82.244 71\"));", "0.943042 -0.251816 0.217389"),
        ("echo(Vector::getRotation(\"0 0 -1\"));", "3.14159 0 1.12949e-38"),
        ("echo(Vector::getRotation(\"0 -2 1\"));", "-1.10714 0 -3.14159"),
        ("echo(Vector::getRotation(\"abc\"));", "0 0 1.12949e-38"),
        ("echo(Vector::getRotation(\"1e-20 1e-20 0\"));", "-1.57079 0 -0.785398"),
        ("echo(Vector::getRotation(\"+INF 1 1\"));", "-1.57079 0 -1.57079"),
        ("echo(Vector::getRotation(\"+NAN 1 1\"));", "0 0 1.12949e-38"),
        ("echo(Vector::getRotation());", "wrong argcount to Vector::getRotation(normal)\n0 0 0"),
        ("echo(Vector::getRotation(\"2.558492e-16 1.9806 8.0727\"));", "-0.240593 0 -1.29177e-16"),
        ("echo(Vector::getRotation(\"-404 48.84129 -170\"));", "-1.96651 0 1.45048"),
        ("echo(Vector::getRotation(\"-317 6.2209 4.9077\"));", "-1.55531 0 1.55117"),
        ("echo(Vector::getRotation(\"9.7316e-25 -704 -9.1205\"));", "-1.58375 0 -3.14159"),
        ("echo(Vector::getRotation(\"9.6537 506 -42\"));", "-1.65359 0 -0.0190761"),
        ("echo(Vector::getRotation(\"-9.1685 203 513\"));", "-0.377152 0 0.0451343"),
        ("echo(Vector::getRotation(\"6.9e-05 -18.2533 -9.0128\"));", "-2.02944 0 -3.14158"),
        ("echo(Vector::getRotation(\"92 -1.0391 -943\"));", "-3.04433 0 -1.58209"),
        ("echo(Vector::getRotation(\"-29 -23.787 64.8\"));", "-0.524701 0 2.25775"),
        ("echo(Vector::getRotation(\"875 -65.1722 -6.9738\"));", "-1.57874 0 -1.64514"),
        ("echo(Vector::getRotation(\"0 1 0\"));", "-1.57079 0 -0"),
        ("echo(Vector::getRotation(\"1 1 1\"));", "-0.955316 0 -0.785398"),
        ("echo(Vector::getRotation(\"-36865.9 -21126 -67082.2\"));", "-2.57697 0 2.09116"),
        ("echo(Vector::getRotation(\"0.0037109 0.0495805 0.0476318\"));", "-0.806836 0 -0.0747066"),
        ("echo(Vector::getRotation(\"1.47163e-08 -2.41649e-08 -1.84232e-08\"));", "-2.14797 0 -2.59458"),
        ("echo(Vector::getRotation(\"-4.79743e-07 -1.38753e-07 1.97977e-06\"));", "-0.247099 0 1.85233"),
        ("echo(Vector::getRotation(\"7.61039e-09 4.77626e-09 -4.22375e-08\"));", "-2.93199 0 -1.01033"),
        ("echo(Vector::getFromRot(\"1 0 0\"));", "0 0.540302 0.84147"),
        ("echo(Vector::getFromRot(\"1.5707963 0 0\"));", "0 7.54978e-08 0.999999"),
        ("echo(Vector::getFromRot(\"0.3 0.5 0.7\", 10, 4));", "-4.01941 7.7488 6.30874"),
        ("echo(Vector::getFromRot(\"1 2\"));", "0 0.540302 0.84147"),
        ("echo(Vector::getFromRot(\"-0 -0 -0\"));", "0 1 0"),
        ("echo(Vector::getFromRot(\"1e-40 0 0\"));", "0 0.999999 9.9998e-41"),
        ("echo(Vector::getFromRot(\"1e19 2 3\"));", "-NAN -NAN -NAN"),
        ("echo(Vector::getFromRot(\"1e30 0 0\"));", "-NAN -NAN -NAN"),
        ("echo(Vector::getFromRot(\"0 0 1e30\"));", "+NAN -NAN 0"),
        ("echo(Vector::getFromRot(\"1e30 1e30 1e30\"));", "+NAN -NAN +NAN"),
        ("echo(Vector::getFromRot(\"1e38 1 1\"));", "-NAN -NAN -NAN"),
        ("echo(Vector::getFromRot(\"1 1 1e38\"));", "+NAN -NAN +NAN"),
        ("echo(Vector::getFromRot(\"+NAN 0 0\"));", "0 1 0"),
        ("echo(Vector::getFromRot());", "Vector::getFromRot: <Rot 3F> [<length>]\nReturns a vector in the direction given by <Rot 3F>,\nof the given length if supplied, else of unit length.\n0 0 0"),
        ("echo(Vector::getFromRot(\"6.92853 4.94639 6.11067\", -1.247, -0.377));", "0.199295 -0.992578 -0.819866"),
        ("echo(Vector::getFromRot(\"4.43697 6.74941 1.26467\", -19.456, 2.856));", "-6.99903 3.55853 18.0288"),
        ("echo(Vector::getFromRot(\"1.04267 -2.09661 -6.56140\", 2.730, 0.331));", "0.141949 1.53939 2.27431"),
        ("echo(Vector::getFromRot(\"-3.82964 -6.37126 0.44162\"));", "0.330164 -0.698375 0.635029"),
        ("echo(Vector::getFromRot(\"-2.04677 0.54898 6.39242\"));", "0.0499522 -0.455473 -0.888847"),
        ("echo(Vector::getFromRot(\"6.56851 3.43354 2.60682\", -17.281, 3.858));", "8.87642 12.8064 -8.40939"),
        ("echo(Vector::getFromRot(\"-3.46048 -1.42753 6.50605\"));", "0.209881 -0.9261 0.31351"),
        ("echo(Vector::getFromRot(\"-6.80100 -0.15949 -3.18675\", -4.420, 2.610));", "0.529873 2.54371 4.42686"),
        ("echo(Vector::getFromRot(\"2.13425 3.14837 -6.46923\"));", "-0.0987957 -0.524892 0.845415"),
        ("echo(Vector::getFromRot(\"1.92211 -0.11483 3.42608\", 0.072, 4.452));", "-0.682832 4.15257 -1.45437"),
        ("echo(Vector::getFromRot(\"1e2 -959 123456.789\"));", "0.861486 0.0378828 -0.506365"),
        ("echo(Vector::getFromRot(\"3.14159265358979 1e-40 -0.0002\"));", "-0.000199999 -0.999999 1.50995e-07"),
        ("echo(Vector::getFromRot(\"7.896 16.628762 58.26178\"));", "0.0415812 0.00595794 0.999117"),
        ("echo(Vector::getFromRot(\"16777217 -366 3.4028236e38\"));", "+NAN -NAN +NAN"),
        ("echo(Vector::getFromRot(\"1e2 123456.789 -432\"));", "-0.861904 0.0267363 -0.506365"),
        ("echo(pow(0.5,3));", "0.125"),
        ("echo(pow(1,3));", "1"),
        ("echo(pow(1.5,1));", "1.5"),
        ("echo(pow(1.5,-13));", "0.00513823"),
        ("echo(pow(2,-7));", "0.0078125"),
        ("echo(pow(3,0.3333333));", "1.44225"),
        ("echo(pow(10,2.5));", "316.228"),
        ("echo(pow(100,10));", "9.99999e+19"),
        ("echo(pow(0.1,-13));", "1.00001e+13"),
        ("echo(pow(0.01,-1));", "100"),
        ("echo(pow(7,1));", "7"),
        ("echo(pow(1e-5,3));", "9.99996e-16"),
        ("echo(pow(1e5,-2));", "9.99999e-11"),
        ("echo(pow(123.456,-0.5));", "0.0900007"),
        ("echo(pow(1.0001,2));", "1.00018"),
        ("echo(pow(1.0001,0.3333333));", "1.00002"),
        ("echo(pow(0.9999,0.5));", "0.999949"),
        ("echo(pow(2.5,10));", "9536.74"),
        ("echo(pow(9,-0.5));", "0.333333"),
        ("echo(pow(0.25,1));", "0.25"),
        ("echo(pow(0.25,2.5));", "0.03125"),
        ("echo(pow(1e10,0.5));", "100000"),
        ("echo(pow(-2,12));", "4096"),
        ("echo(pow(-8,-1));", "-0.125"),
        ("echo(pow(-0.5,2));", "0.25"),
        ("echo(pow(-0.5,12));", "0.00024414"),
        ("echo(pow(-3,0));", "1"),
        ("echo(pow(-3,11));", "-177147"),
        ("echo(pow(-10,2));", "100"),
        ("echo(pow(-10,12));", "9.99999e+11"),
        ("echo(pow(-1,1));", "-1"),
        ("echo(pow(-1,13));", "-1"),
        ("echo(pow(-1,5001));", "-1"),
        ("echo(pow(-1.0001,11));", "-1.00099"),
        ("echo(pow(-1.0001,101));", "-1.00913"),
        ("echo(pow(-0.9999,1));", "-0.999898"),
        ("echo(pow(-0.9999,13));", "-0.998687"),
        ("echo(pow(-0.9999,5001));", "-0.603429"),
        ("echo(pow(\"1.0001\",\"-5000\"));", "0.606546"),
        ("echo(pow(\"-1\",\"10000\"));", "1"),
        ("echo(pow(\"0.99999999\",\"100000\"));", "0.999"),
        ("echo(pow(\"10\",\"-45\"));", "0"),
        ("echo(pow(\"2\",\"129\"));", "3.40282e+38"),
        ("echo(pow(\"2\",\"-1000\"));", "0"),
        ("echo(pow(\"1e-310\",\"2\"));", "0"),
        ("echo(pow(\"-0\",\"3\"));", "0"),
        ("echo(pow(\"\",\"3\"));", "0"),
        ("echo(pow(\"2\",\"-1e-5\"));", "0.999993"),
        ("echo(pow(\"4\",\"0.5\"));", "2"),
        ("echo(pow(\"1000\",\"0.3333333\"));", "10"),
        ("echo(pow(\"3\",\"0.5\"));", "1.73205"),
        ("echo(pow(\"2\",\"63\"));", "9.22337e+18"),
        ("echo(pow(\"5\",\"1.5\"));", "11.1803"),
        ("echo(pow(14.604,-0.25));", "0.511544"),
        ("echo(pow(17.605,-15));", "2.06774e-19"),
        ("echo(pow(13.396,-1.29));", "0.0351737"),
        ("echo(pow(9.843,0.20));", "1.57988"),
        ("echo(pow(2.415,1.67));", "4.35978"),
        ("echo(pow(3.135,-2.0795));", "0.0929141"),
        ("echo(pow(613.9233,-1.09));", "0.000914079"),
        ("echo(pow(11.012,-4.1388));", "4.87473e-05"),
        ("echo(pow(3.077,4.7973));", "219.625"),
        ("echo(pow(315.8777,1.01));", "334.571"),
        ("echo(pow(1.024037,3.9268));", "1.09773"),
        ("echo(pow(2.522,1.6118));", "4.44146"),
        ("echo(pow(1.025756,3.4315));", "1.09116"),
        ("echo(pow(12,-4.2092));", "2.86762e-05"),
        ("echo(pow(28,4.4270));", "2.55008e+06"),
        ("echo(pow(-7,-13));", "-1.0321e-11"),
        ("echo(pow(-13,-20));", "5.26178e-23"),
        ("echo(pow(-15,-10));", "1.73415e-12"),
        ("echo(pow(-9,5));", "-59049"),
        ("echo(pow(0.991915,14822));", "0"),
        ("echo(pow(1.009287,9050));", "2.02111e+36"),
        ("echo(pow(1.006619,25136));", "+INF"),
        ("echo(POW(2,3));", "8"),
        ("echo(pow(2,0.5));", "1.41421"),
        ("echo(sprintf(\"a%1b\",\"X\"));", "aXb"),
        ("echo(sprintf(\"%1%2%3%4%5%6%7%8%9\",\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\"));", "wrong argcount to sprintf(string, [Arg1 ... Arg9] )\nfalse"),
        ("echo(sprintf(\"%9%8%7%6%5%4%3%2%1\",\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\"));", "wrong argcount to sprintf(string, [Arg1 ... Arg9] )\nfalse"),
        ("echo(sprintf(\"%1\",\"x y z\"));", "x y z"),
        ("echo(sprintf(\"%1%2\",1.5,-0.0));", "1.5-0"),
        ("echo(sprintf(\"tab\\there %1\",\"x\"));", "tab\there x"),
        ("echo(sprintf(\"%1%2%3%4%5%6%7%8%9\",\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\",\"8\",\"9\",\"extra\"));", "wrong argcount to sprintf(string, [Arg1 ... Arg9] )\nfalse"),
        ("echo(sprintf(\"%1\",\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\",\"j\"));", "wrong argcount to sprintf(string, [Arg1 ... Arg9] )\nfalse"),
        ("echo(sprintf(\"%1%2\",\"%2\",\"y\"));", "%2y"),
        ("echo(sprintf(\"%1%2%3\",\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\"));", "ccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
        ("echo(sprintf());", "wrong argcount to sprintf(string, [Arg1 ... Arg9] )\nfalse"),
        ("echo(sprintf(\"%1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\",\"8\",\"9\",\"10\",\"11\",\"12\"));", "wrong argcount to sprintf(string, [Arg1 ... Arg9] )\nfalse"),
        ("echo(sprintf(\"%4:%8:%5%3:%4\", \"x\", \"-3.5\", \"x\", \"x\", \"0\", \"0\", \"-3.5\", \"abc\", \"12\"));", "wrong argcount to sprintf(string, [Arg1 ... Arg9] )\nfalse"),
        ("echo(sprintf(\"text  --  -\", \"ZZ\", \"ZZ\", \"x\", \"a b\", \"ZZ\", \"a b\", \"\", \"12\", \"12\"));", "wrong argcount to sprintf(string, [Arg1 ... Arg9] )\nfalse"),
        ("echo(sprintf(\"-text-\", \"12\", \"-3.5\", \"0\", \"\", \"abc\", \"abc\", \"a b\", \"x\", \"\"));", "wrong argcount to sprintf(string, [Arg1 ... Arg9] )\nfalse"),
        ("echo(sprintf(\"%1-%1\", \"\", \"ZZ\"));", "-"),
        ("echo(sprintf(\"--%6:%7\", \"x\", \"\", \"-3.5\", \"abc\", \"-3.5\", \"abc\", \"ZZ\", \"12\"));", "--abc:ZZ"),
        ("echo(sprintf(\":\", \"a b\"));", ":"),
        ("echo(getIntegerTime(\"false\"));", "0.0"),
        ("echo(getIntegerTime(\"\"));", "0.0"),
        ("echo(TIMESTAMP());", "11:55:21 Apr 26 2000"),
        ("echo(which(\"*.cs\"));", "Couldn't find file: *.cs"),
        ("echo(findPointOnSphere());", "findPointOnSphere: <playerPos, objectPos, vec, object>\n0 0 0"),
        ("echo(findPointOnSphere(\"1 2 3\",\"4 5 6\",\"0 0 1\",0));", "0 0 0"),
        ("echo(findPointOnSphere(\"1 2 3\",\"4 5 6\",\"0 0 1\",1,2));", "findPointOnSphere: <playerPos, objectPos, vec, object>\n0 0 0"),
        ("echo(getClientByName());", "0"),
        ("echo(getObjectByTargetIndex());", "-1"),
        ("echo(Server::ResortList(\"x\"));", "Server::ResortList: Unknown command.\nFalse"),
    };

    // Rows that call game-state commands (P4).
    private static readonly HashSet<string> kGameRows = new()
    {
        "echo(getIntegerTime(\"false\"));",
        "echo(getIntegerTime(\"\"));",
        "echo(findPointOnSphere());",
        "echo(findPointOnSphere(\"1 2 3\",\"4 5 6\",\"0 0 1\",0));",
        "echo(findPointOnSphere(\"1 2 3\",\"4 5 6\",\"0 0 1\",1,2));",
        "echo(getClientByName());",
        "echo(getObjectByTargetIndex());",
    };

    // Outputs of the double form where the reference output needs the 80-bit round-toward-zero unit.
    private static readonly Dictionary<string, string> kDouble = new()
    {
        ["echo(Vector::add(\"1e-40 -5.681509e+07 -833301.576\",\"-848 -3992 4.794e-13\"));"] = "-848 -5.6819e+07 -833301", // reference "-847.999 -5.6819e+07 -833301"
        ["echo(Vector::sub(\"-NAN 1 1\",\"+NAN 1 1\"));"] = "-NAN 0 0", // reference "+NAN 0 0"
        ["echo(Vector::sub(\"1e2 0.1 9.409824e-11\",\"-82 0.004 1e-40\"));"] = "182 0.096 9.40982e-11", // reference "182 0.0959999 9.40982e-11"
        ["echo(Vector::sub(\"-1e-40 -302 4.688e-09\",\"277 -49 -6.8689e+25\"));"] = "-277 -253 6.8689e+25", // reference "-277 -253 6.86889e+25"
        ["echo(Vector::sub(\"1E-2 102 -73.717879\",\"85.76797 1e-30 6e+04\"));"] = "-85.7579 102 -60073.7", // reference "-85.7579 101.999 -60073.7"
        ["echo(Vector::sub(\"-7.99318e+09 45.97874 167\",\"-46.1907 -6.726197e-19 813\"));"] = "-7.99318e+09 45.9787 -646", // reference "-7.99317e+09 45.9787 -646"
        ["echo(Vector::dot(\"+INF -INF +NAN\",\"1 1 1\"));"] = "-NAN", // reference "+NAN"
        ["echo(Vector::getDistance(\"-NAN 1 1\",\"+NAN 1 1\"));"] = "-NAN", // reference "+NAN"
        ["echo(Vector::neg(\"1e30 1e30 1e30\"));"] = "-1e+30 -1e+30 -1e+30", // reference "-9.99999e+29 -9.99999e+29 -9.99999e+29"
        ["echo(Vector::neg(\"233 -6.993427e-26 5.1e+37\"));"] = "-233 6.99342e-26 -5.1e+37", // reference "-233 6.99342e-26 -5.09999e+37"
        ["echo(Vector::neg(\"3.160 16777217 -83.456\"));"] = "-3.16 -1.67772e+07 83.456", // reference "-3.15999 -1.67772e+07 83.4559"
        ["echo(Vector::normalize(\"1e-40 0 0\"));"] = "+INF -NAN -NAN", // reference "0.034028 0 0"
        ["echo(Vector::normalize(\"9.362e-25 -7.3e-13 3.14159265358979\"));"] = "2.98001e-25 -2.32366e-13 1", // reference "2.98001e-25 -2.32366e-13 0.999999"
        ["echo(Vector::normalize(\"89.2 6.76e-12 -4.93e+09\"));"] = "1.80933e-08 1.37119e-21 -1", // reference "1.80933e-08 1.37119e-21 -0.999999"
        ["echo(Vector::normalize(\"44.8 -37.50135 -1.63e+14\"));"] = "2.74846e-13 -2.30069e-13 -1", // reference "2.74846e-13 -2.30069e-13 -0.999999"
        ["echo(Vector::normalize(\"-720 -4.26264e-10 1.58451261e-12\"));"] = "-1 -5.92033e-13 2.20071e-15", // reference "-0.999999 -5.92033e-13 2.20071e-15"
        ["echo(Vector::getFromRot(\"1.5707963 0 0\"));"] = "0 7.54979e-08 1", // reference "0 7.54978e-08 0.999999"
        ["echo(Vector::getFromRot(\"1e-40 0 0\"));"] = "0 1 9.99994e-41", // reference "0 0.999999 9.9998e-41"
        ["echo(Vector::getFromRot(\"1e19 2 3\"));"] = "-0.115149 -0.807801 0.578097", // reference "-NAN -NAN -NAN"
        ["echo(Vector::getFromRot(\"1e30 0 0\"));"] = "0 -0.611604 -0.791163", // reference "-NAN -NAN -NAN"
        ["echo(Vector::getFromRot(\"0 0 1e30\"));"] = "0.791163 -0.611604 0", // reference "+NAN -NAN 0"
        ["echo(Vector::getFromRot(\"1e30 1e30 1e30\"));"] = "-0.483879 0.37406 -0.791163", // reference "+NAN -NAN +NAN"
        ["echo(Vector::getFromRot(\"1e38 1 1\"));"] = "-0.123537 0.0793226 0.989164", // reference "-NAN -NAN -NAN"
        ["echo(Vector::getFromRot(\"1 1 1e38\"));"] = "-0.534447 0.0793226 0.84147", // reference "+NAN -NAN +NAN"
        ["echo(Vector::getFromRot(\"6.92853 4.94639 6.11067\", -1.247, -0.377));"] = "0.199295 -0.992585 -0.819873", // reference "0.199295 -0.992578 -0.819866"
        ["echo(Vector::getFromRot(\"4.43697 6.74941 1.26467\", -19.456, 2.856));"] = "-6.99902 3.55853 18.0288", // reference "-6.99903 3.55853 18.0288"
        ["echo(Vector::getFromRot(\"-2.04677 0.54898 6.39242\"));"] = "0.0499522 -0.455473 -0.888846", // reference "0.0499522 -0.455473 -0.888847"
        ["echo(Vector::getFromRot(\"6.56851 3.43354 2.60682\", -17.281, 3.858));"] = "8.87642 12.8064 -8.40941", // reference "8.87642 12.8064 -8.40939"
        ["echo(Vector::getFromRot(\"-3.46048 -1.42753 6.50605\"));"] = "0.209881 -0.926099 0.31351", // reference "0.209881 -0.9261 0.31351"
        ["echo(Vector::getFromRot(\"2.13425 3.14837 -6.46923\"));"] = "-0.098796 -0.524892 0.845415", // reference "-0.0987957 -0.524892 0.845415"
        ["echo(Vector::getFromRot(\"1.92211 -0.11483 3.42608\", 0.072, 4.452));"] = "-0.682832 4.15258 -1.45437", // reference "-0.682832 4.15257 -1.45437"
        ["echo(Vector::getFromRot(\"1e2 -959 123456.789\"));"] = "0.861164 0.044612 -0.506365", // reference "0.861486 0.0378828 -0.506365"
        ["echo(Vector::getFromRot(\"3.14159265358979 1e-40 -0.0002\"));"] = "-0.000199999 -1 -8.74227e-08", // reference "-0.000199999 -0.999999 1.50995e-07"
        ["echo(Vector::getFromRot(\"16777217 -366 3.4028236e38\"));"] = "-NAN -NAN -0.779563", // reference "+NAN -NAN +NAN"
        ["echo(pow(3,0.3333333));"] = "1.44224", // reference "1.44225"
        ["echo(pow(10,2.5));"] = "316.227", // reference "316.228"
        ["echo(pow(100,10));"] = "1e+20", // reference "9.99999e+19"
        ["echo(pow(0.1,-13));"] = "9.99999e+12", // reference "1.00001e+13"
        ["echo(pow(1e-5,3));"] = "1e-15", // reference "9.99996e-16"
        ["echo(pow(1e5,-2));"] = "1e-10", // reference "9.99999e-11"
        ["echo(pow(123.456,-0.5));"] = "0.0900002", // reference "0.0900007"
        ["echo(pow(1.0001,0.3333333));"] = "1.00003", // reference "1.00002"
        ["echo(pow(-0.9999,1));"] = "-0.999899", // reference "-0.999898"
        ["echo(pow(-0.9999,13));"] = "-0.9987", // reference "-0.998687"
        ["echo(pow(-0.9999,5001));"] = "-0.606454", // reference "-0.603429"
        ["echo(pow(\"1.0001\",\"-5000\"));"] = "0.606545", // reference "0.606546"
        ["echo(pow(\"10\",\"-45\"));"] = "1.40129e-45", // reference "0"
        ["echo(pow(\"2\",\"129\"));"] = "+INF", // reference "3.40282e+38"
        ["echo(pow(\"1000\",\"0.3333333\"));"] = "9.99999", // reference "10"
        ["echo(pow(14.604,-0.25));"] = "0.511543", // reference "0.511544"
        ["echo(pow(17.605,-15));"] = "2.06756e-19", // reference "2.06774e-19"
        ["echo(pow(13.396,-1.29));"] = "0.0351724", // reference "0.0351737"
        ["echo(pow(2.415,1.67));"] = "4.35981", // reference "4.35978"
        ["echo(pow(613.9233,-1.09));"] = "0.00091402", // reference "0.000914079"
        ["echo(pow(11.012,-4.1388));"] = "4.87455e-05", // reference "4.87473e-05"
        ["echo(pow(315.8777,1.01));"] = "334.59", // reference "334.571"
        ["echo(pow(12,-4.2092));"] = "2.86754e-05", // reference "2.86762e-05"
        ["echo(pow(1.009287,9050));"] = "2.0211e+36", // reference "2.02111e+36"
    };

    // Console output without trailing blank lines (an empty result prints an empty line).
    private static string Run(TestWorld w, string s)
    {
        string output = w.Run(s);
        while (output.Length != 0 && output[^1] == '\n')
            output = output[..^1];
        return output;
    }

    [Fact]
    public void TestMathCommands()
    {
        string root = TestPaths.TmpDir("math_commands");
        Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "base"));
        var w = new TestWorld(root);

        // Reference outputs, one console line each.
        var failures = new List<string>();
        foreach (var (script, reference) in kRows)
        {
            if (kGameRows.Contains(script))
                continue;
            string want = kDouble.TryGetValue(script, out string? d) ? d : reference;
            string got = Run(w, script);
            if (got != want)
                failures.Add("FAIL " + script + "\n  want: " + want + "\n  got:  " + got);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // getRandom: R250 seeded with 0x8377 on first use, never reseeded (0x005eb5a0, 0x005eb660).
        // The first values of a freshly started reference server.
        {
            var fresh = new TestWorld(root);
            string[] want = { "0.86173", "0.294385", "0.464783", "0.300487" };
            foreach (string want1 in want)
                Assert.Equal(want1, Run(fresh, "echo(getRandom());"));
            var rng = new RandomR250();
            Assert.True(rng.Next() == 0xdc9a6435u && rng.Next() == 0x4b5cdef3u && rng.Next() == 0x76fc08c5u);
            // Index 249 wraps to 0 after 250 values.
            var wrap = new RandomR250();
            var v = new uint[254];
            for (int i = 0; i < v.Length; ++i)
                v[i] = wrap.Next();
            Assert.True(v[248] == 0x3de60101u && v[249] == 0xc27fa774u && v[250] == 0x47760a6eu && v[253] == 0x69b7548au);
            var other = new RandomR250(1);
            Assert.True(other.Next() == 0xe56e2245u && other.Next() == 0x77be0fafu);
            other.Reseed(0x8377);
            Assert.Equal(0xdc9a6435u, other.Next());
        }

        // Component parsing (sscanf "%f %f %f"): 1e, 1e+ are numbers; hex, inf and nan are not;
        // values from 2^128 up are infinite, the rest convert toward zero (to nearest in the double form).
        {
            float[] v = { 7, 8, 9 };
            Assert.True(MathCommands.ScanFloats3("1e 1e+ 1e+5", v) == 3 && v[0] == 1 && v[1] == 1 && v[2] == 100000);
            float[] u = { 7, 8, 9 };
            Assert.True(MathCommands.ScanFloats3("0x10", u) == 1 && u[0] == 0 && u[1] == 8 && u[2] == 9);
            Assert.True(MathCommands.ScanFloats3("inf", u) == 0 && MathCommands.ScanFloats3("", u) == 0);
            Assert.True(MathCommands.ScanFloats3(".", u) == 0 && MathCommands.ScanFloats3("1,2", u) == 1);
            var f = new float[3];
            Assert.Equal(3, MathCommands.ScanFloats3("3.4028236e38 3.4028237e38 -1e39", f));
            // Reference (toward zero): f[0] == 3.40282347e38f (FLT_MAX). To nearest 3.4028236e38 is above the midpoint of
            // FLT_MAX and 2^128 and stores as infinity.
            Assert.True(float.IsPositiveInfinity(f[0]) && float.IsPositiveInfinity(f[1]) && float.IsNegativeInfinity(f[2]));
            var t = new float[3];
            Assert.Equal(3, MathCommands.ScanFloats3("0.1 1e-40 1.0000001", t));
            // Reference (toward zero): t[0] == 0.0999999940395355f, t[2] == 1.0f. To nearest: 0.1f and 1.00000012f.
            Assert.True(t[0] == 0.1f && t[1] > 0 && t[1] <= 1.0e-40f && t[2] == 1.00000012f);
        }

        // sprintf: DECISIONS D11 cases (the original hangs or reads stale pointers).
        {
            string[] a = { "fmt", "X", "Y" };
            Assert.Equal("100%", MathCommands.FormatSprintf("100%", a));
            Assert.Equal("a%xb%0c%", MathCommands.FormatSprintf("a%xb%0c%", a));
            Assert.Equal("[X][Y][][]", MathCommands.FormatSprintf("[%1][%2][%3][%9]", a));
            string[] big = { "", new string('z', 2000) };
            Assert.Equal(1063, MathCommands.FormatSprintf("%1%1", big).Length);
            Assert.Equal(1063, MathCommands.FormatSprintf(new string('q', 1100), big).Length);
            Assert.Equal("a %", Run(w, "echo(sprintf(\"%1 %\", \"a\"));"));
        }

        // pow (Borland 0x00606d90): the error paths of the original never return on the dedicated
        // server (a message box); they report the value its handler would return.
        {
            bool err;
            Assert.True(MathCommands.BorlandPow(2, 10, out err) == 1024 && !err);
            Assert.True(MathCommands.BorlandPow(0, 2, out err) == 0 && !err);
            Assert.True(MathCommands.BorlandPow(1e-310, 2, out err) == 0 && !err);
            Assert.True(MathCommands.BorlandPow(5, 0, out err) == 1 && !err);
            Assert.True(MathCommands.BorlandPow(0, 0, out err) == 1 && err);
            Assert.True(double.IsInfinity(MathCommands.BorlandPow(0, -1, out err)) && err);
            Assert.True(double.IsNaN(MathCommands.BorlandPow(-8, 0.5, out err)) && err);
            Assert.True(double.IsInfinity(MathCommands.BorlandPow(2, 2000, out err)) && err);
            Assert.True(MathCommands.BorlandPow(2, -2000, out err) == 0 && err);
            Assert.True(double.IsInfinity(MathCommands.BorlandPow(double.PositiveInfinity, 1, out err)) && err);
            Assert.True(MathCommands.BorlandPow(double.PositiveInfinity, -1, out err) == 0 && err);
            Assert.True(double.IsInfinity(MathCommands.BorlandPow(2, double.PositiveInfinity, out err)) && err);
            Assert.True(MathCommands.BorlandPow(-3, 3, out err) == -27 && !err);
            // The C++ checks here that a fractional power leaves the FPU rounding to nearest until the next
            // statement (two fegetround asserts). C# has no FPU rounding mode: not portable.
        }

        // getRotation divides a zero horizontal part by zero in the original (math error, D11): -0.
        Assert.Equal("-0 0 -1.57079", Run(w, "echo(Vector::getRotation(\"1e-30 0 0\"));"));

        // findPointOnSphere (kSphere), the shape header radius (readShapeRadius) and the game-state commands
        // (getIntegerTime, getClientByName, getNumItems, getObjectByTargetIndex) need the game modules: P4.
    }
}
