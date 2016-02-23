var array: [int]int;
const n:int;
const x:int; 
const y:int;


procedure ReplaceChar()
{  
   call Helper(0);
}

procedure Helper(i:int) {
   var tmp:int;

   if (i < n && array[i] != -1) {
       tmp := array[i];
       //havoc tmp; //fault
       array[i] := if (tmp == x) then y else tmp; 
       call Helper(i+1);
   }
}


//auxiliary
function {:inline true} ArrEqAfter(a: [int]int, b: [int]int, i: int) : bool
{
  (forall j: int :: { a[j] } { b[j] } j >= i ==> a[j] == b[j])
}
/*
procedure Test(a:[int]int, b:[int]int, i:int)
requires ArrEqAfter(a,b,i);
{           
    var ta, tb : [int]int;
    ta, tb := a, b;

    ta[i] := 1;
    tb[i] := 1;
    assert  ArrEqAfter(ta,tb,i+1);
}
*/
